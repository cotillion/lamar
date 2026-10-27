using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lamar.IoC;
using Lamar.IoC.Frames;
using Lamar.IoC.Instances;
using Lamar.Scanning.Conventions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Lamar.Testing.Bugs;

public class Bug_concurrent_singleton_resolution
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public async Task quick_and_generated_resolution_share_one_construction(ServiceLifetime lifetime)
    {
        using var building = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var attempting = new ManualResetEventSlim();
        var builds = 0;
        var instance = new ConstructorInstance(typeof(DisposableService), typeof(DisposableService), lifetime);
        using var container = Container.For(x =>
        {
            x.Add(instance);
            x.AddSingleton(new BuildAction(() =>
            {
                if (Interlocked.Increment(ref builds) == 1)
                {
                    building.Set();
                    proceed.Wait(Timeout).ShouldBeTrue();
                }
            }));
        });

        // Build this delegate without installing _resolver. The quick path and generated path must
        // coordinate even if generation finishes while a quick construction is in progress.
        var generated = instance.BuildFuncResolver(container);
        var quick = Task.Factory.StartNew(() => instance.QuickResolve(container), TaskCreationOptions.LongRunning);
        Task<object> other;
        try
        {
            building.Wait(Timeout).ShouldBeTrue();
            other = Task.Factory.StartNew(() =>
            {
                attempting.Set();
                return generated(container);
            }, TaskCreationOptions.LongRunning);
            attempting.Wait(Timeout).ShouldBeTrue();

            // A different construction lock lets the second call complete while the first constructor
            // is still gated. A shared lock keeps it pending until proceed is released.
            (await Task.WhenAny(other, Task.Delay(100))).ShouldNotBe(other);
        }
        finally
        {
            proceed.Set();
        }

        var results = await Task.WhenAll(quick, other).WaitAsync(Timeout);
        results[1].ShouldBeSameAs(results[0]);
        container.GetInstance<DisposableService>().ShouldBeSameAs(results[0]);
        container.GetInstance<DisposableService>(instance.Name).ShouldBeSameAs(results[0]);
        builds.ShouldBe(1);
        container.Disposables.Count.ShouldBe(1);
        container.Dispose();
        ((DisposableService)results[0]).Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task parallel_first_resolutions_preserve_every_service_and_disposable()
    {
        // Mix quick constructor builds, generated builds, and singleton/scoped lambda resolvers.
        // Scoped services are deliberately resolved from the root to exercise the same shared map.
        const int count = 24;
        using var ready = new CountdownEvent(count);
        using var proceed = new ManualResetEventSlim();
        var builds = 0;
        var build = new BuildAction(() =>
        {
            // Duplicates must not participate in the first wave's rendezvous.
            if (Interlocked.Increment(ref builds) <= count)
            {
                ready.Signal();
                proceed.Wait(Timeout).ShouldBeTrue();
            }
        });
        var markers = new List<Type>();
        var marker = typeof(int);
        for (var i = 0; i < count; i++)
        {
            markers.Add(marker);
            marker = marker.MakeArrayType();
        }

        var serviceTypes = markers.Select(t => typeof(Service<>).MakeGenericType(t)).ToArray();
        var consumerTypes = markers.Select(t => typeof(Consumer<>).MakeGenericType(t)).ToArray();
        using var container = Container.For(x =>
        {
            x.AddSingleton(build);
            for (var i = 0; i < count; i++)
            {
                var type = serviceTypes[i];
                switch (i % 4)
                {
                    case 0:
                        x.AddSingleton(type, type);
                        break;
                    case 1:
                        x.AddScoped(type, type);
                        break;
                    case 2:
                        x.AddSingleton(type, _ => Activator.CreateInstance(type, build));
                        break;
                    default:
                        x.AddScoped(type, _ => Activator.CreateInstance(type, build));
                        break;
                }

                x.AddTransient(consumerTypes[i], consumerTypes[i]);
            }
        });

        var tasks = consumerTypes.Select(type => Task.Factory.StartNew(
            () => (IConsumer)container.GetInstance(type), TaskCreationOptions.LongRunning)).ToArray();
        try
        {
            ready.Wait(Timeout).ShouldBeTrue("all registrations should construct independently");
        }
        finally
        {
            proceed.Set();
        }

        var consumers = await Task.WhenAll(tasks).WaitAsync(Timeout);
        for (var i = 0; i < count; i++)
        {
            container.GetInstance(serviceTypes[i]).ShouldBeSameAs(consumers[i].Value);
            ((IConsumer)container.GetInstance(consumerTypes[i])).Value.ShouldBeSameAs(consumers[i].Value);
        }

        builds.ShouldBe(count);
        container.Disposables.Count.ShouldBe(count);
        container.Dispose();
        foreach (var consumer in consumers) consumer.Value.Disposals.ShouldBe(1);
    }

    [Fact]
    public void null_fallback_does_not_repeat_instance_resolution()
    {
        var instance = new CountingNullInstance();
        using var container = Container.For(x => x.Add(instance));
        var resolver = container.Model.For<NullConsumer>().Default.Instance.ToResolver(container);

        ((NullConsumer)resolver(container)).Value.ShouldBeNull();
        ((NullConsumer)resolver(container)).Value.ShouldBeNull();
        instance.Resolutions.ShouldBe(1);
    }

    [Fact]
    public void referenced_transients_preserve_constant_singleton_injection()
    {
        var builds = 0;
        using var container = Container.For(x =>
        {
            x.For<Leaf>().Use(_ =>
            {
                builds++;
                return new Leaf();
            }).Singleton();
            x.For<Middle>().Use<Middle>().Named("middle");
            x.For<Outer>().Use<Outer>().Ctor<Middle>().IsNamedInstance("middle");
        });

        var resolver = container.ServiceGraph.FindResolver(typeof(Outer));
        builds.ShouldBe(1); // Built during preparation, before executing the generated consumer.
        var first = (Outer)resolver(container);
        ((Outer)resolver(container)).Value.Value.ShouldBeSameAs(first.Value.Value);
        builds.ShouldBe(1);

        var leaf = container.Model.For<Leaf>().Default.Instance;
        var expression = new InjectedServiceField(leaf).ToVariableExpression(
            new JasperFx.CodeGeneration.Expressions.LambdaDefinition { Context = container });
        expression.ShouldBeOfType<System.Linq.Expressions.ConstantExpression>();
    }

    [Fact]
    public void preparation_does_not_build_lazy_or_func_targets()
    {
        var builds = 0;
        using var container = Container.For(x => x.For<Leaf>().Use(_ =>
        {
            builds++;
            return new Leaf();
        }).Singleton());

        var consumer = container.GetInstance<DeferredConsumer>();
        builds.ShouldBe(0);
        consumer.Lazy.Value.ShouldBeSameAs(consumer.Factory());
        builds.ShouldBe(1);
    }

    private class CountingNullInstance : LambdaInstance<IServiceProvider, Leaf>
    {
        public int Resolutions;

        public CountingNullInstance() : base(typeof(Leaf), _ => null, ServiceLifetime.Singleton) { }

        public override object QuickResolve(Scope scope)
        {
            Resolutions++;
            return base.QuickResolve(scope);
        }
    }

    public record BuildAction(Action Run);

    public class DisposableService : IDisposable
    {
        public int Disposals;
        public DisposableService(BuildAction build) => build.Run();
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    public class Service<T> : DisposableService
    {
        public Service(BuildAction build) : base(build) { }
    }

    public interface IConsumer { DisposableService Value { get; } }

    public class Consumer<T> : IConsumer
    {
        public Consumer(Service<T> value) => Value = value;
        public DisposableService Value { get; }
    }

    public class Leaf { }
    public record NullConsumer(Leaf Value);
    public record Middle(Leaf Value);
    public record Outer(Middle Value);
    public record DeferredConsumer(Lazy<Leaf> Lazy, Func<Leaf> Factory);
}
