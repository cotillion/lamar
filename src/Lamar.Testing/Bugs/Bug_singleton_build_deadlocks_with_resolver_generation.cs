using System;
using System.Threading;
using System.Threading.Tasks;
using Lamar.IoC;
using Lamar.IoC.Instances;
using Lamar.Scanning.Conventions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Lamar.Testing.Bugs;

// Generating the resolver of a service holds the service graph lock. If the resolver injected a singleton that another
// thread was building at the time, and that build resolved a type the container hadn't resolved before (which needs the
// service graph lock), the two threads deadlocked, and so did every later resolution of a new type.
public class Bug_singleton_build_deadlocks_with_resolver_generation
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task lambda_singleton_that_resolves_while_a_consumer_resolver_is_generated()
    {
        using var building = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var requested = new ManualResetEventSlim();

        using var container = Container.For(x =>
        {
            x.Add(new SignalingLambda(c =>
            {
                building.Set();
                proceed.Wait(Timeout).ShouldBeTrue();
                return new Slow(c.GetInstance<Dependency>());
            }, requested));

            x.For<Dependency>().Use<Dependency>();
            x.For<Consumer>().Use<Consumer>();
        });

        await shouldNotDeadlock(container, building, proceed, requested);
    }

    [Fact]
    public async Task constructor_singleton_that_resolves_from_the_container_while_a_consumer_resolver_is_generated()
    {
        using var building = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var requested = new ManualResetEventSlim();

        using var container = Container.For(x =>
        {
            x.AddSingleton(new Gates(building, proceed));
            x.Add(new SignalingConstructor(requested));

            x.For<Dependency>().Use<Dependency>();
            x.For<Consumer>().Use<Consumer>();
        });

        await shouldNotDeadlock(container, building, proceed, requested);
    }

    [Fact]
    public void injected_singletons_are_built_once_including_value_types()
    {
        var builds = 0;
        var container = Container.For(x =>
        {
            x.For<Slow>().Use(_ =>
            {
                Interlocked.Increment(ref builds);
                return new Slow(new Dependency());
            }).Singleton();
            x.AddSingleton(typeof(Point), _ => new Point(3, 4));
            x.For<Consumer>().Use<Consumer>();
            x.For<PointConsumer>().Use<PointConsumer>();
        });

        var first = container.GetInstance<Consumer>();
        container.GetInstance<Consumer>().Slow.ShouldBeSameAs(first.Slow);
        container.GetInstance<Slow>().ShouldBeSameAs(first.Slow);
        builds.ShouldBe(1);

        container.GetInstance<PointConsumer>().Point.ShouldBe(new Point(3, 4));
    }

    [Fact]
    public void resolver_generated_before_its_singletons_are_built_resolves_them_when_it_runs()
    {
        var builds = 0;
        var container = Container.For(x =>
        {
            x.For<Slow>().Use(_ =>
            {
                Interlocked.Increment(ref builds);
                return new Slow(new Dependency());
            }).Singleton();
            x.AddSingleton(typeof(Point), _ => new Point(3, 4));
            x.For<Consumer>().Use<Consumer>();
            x.For<PointConsumer>().Use<PointConsumer>();
        });

        // Generating a resolver through the model doesn't build the singletons first, so it can't inline them
        var resolver = container.Model.For<Consumer>().Default.Instance.ToResolver(container);
        var pointResolver = container.Model.For<PointConsumer>().Default.Instance.ToResolver(container);
        builds.ShouldBe(0);

        var first = (Consumer)resolver(container);
        ((Consumer)resolver(container)).Slow.ShouldBeSameAs(first.Slow);
        container.GetInstance<Slow>().ShouldBeSameAs(first.Slow);
        builds.ShouldBe(1);

        ((PointConsumer)pointResolver(container)).Point.ShouldBe(new Point(3, 4));
    }

    public record struct Point(int X, int Y);

    public class PointConsumer
    {
        public PointConsumer(Point point)
        {
            Point = point;
        }

        public Point Point { get; }
    }

    private static async Task shouldNotDeadlock(IContainer container, ManualResetEventSlim building,
        ManualResetEventSlim proceed, ManualResetEventSlim requested)
    {
        // Builds the singleton, and waits inside the build until the other thread generates the consumer's resolver
        var singleton = Task.Factory.StartNew(() => container.GetInstance<Slow>(), TaskCreationOptions.LongRunning);
        Task<Consumer> consumer;
        try
        {
            building.Wait(Timeout).ShouldBeTrue();
            consumer = Task.Factory.StartNew(() => container.GetInstance<Consumer>(), TaskCreationOptions.LongRunning);

            // The consumer has entered this exact singleton's QuickResolve. Before the fix this entry
            // occurred under _familyLock; it could not release that lock until the singleton completed.
            requested.Wait(Timeout).ShouldBeTrue("the consumer never requested the singleton");
        }
        finally
        {
            proceed.Set();
        }

        await Task.WhenAll(singleton, consumer).WaitAsync(Timeout);
        (await consumer).Slow.ShouldBeSameAs(await singleton);
    }

    private class SignalingLambda : LambdaInstance<IServiceContext, Slow>
    {
        private readonly ManualResetEventSlim _requested;

        public SignalingLambda(Func<IServiceContext, Slow> factory, ManualResetEventSlim requested)
            : base(typeof(Slow), factory, ServiceLifetime.Singleton)
        {
            _requested = requested;
        }

        public override object QuickResolve(Scope scope)
        {
            _requested.Set();
            return base.QuickResolve(scope);
        }
    }

    private class SignalingConstructor : ConstructorInstance
    {
        private readonly ManualResetEventSlim _requested;

        public SignalingConstructor(ManualResetEventSlim requested)
            : base(typeof(Slow), typeof(SlowWithServiceProvider), ServiceLifetime.Singleton)
        {
            _requested = requested;
        }

        public override object QuickResolve(Scope scope)
        {
            _requested.Set();
            return base.QuickResolve(scope);
        }
    }

    public class Dependency
    {
    }

    public class Slow
    {
        public Slow(Dependency dependency)
        {
            Dependency = dependency;
        }

        public Dependency Dependency { get; }
    }

    public record Gates(ManualResetEventSlim Building, ManualResetEventSlim Proceed);

    public class SlowWithServiceProvider : Slow
    {
        public SlowWithServiceProvider(IServiceProvider services, Gates gates)
            : base(build(services, gates))
        {
        }

        private static Dependency build(IServiceProvider services, Gates gates)
        {
            gates.Building.Set();
            gates.Proceed.Wait(Timeout).ShouldBeTrue();
            return services.GetRequiredService<Dependency>();
        }
    }

    public class Consumer
    {
        public Consumer(Slow slow)
        {
            Slow = slow;
        }

        public Slow Slow { get; }
    }
}
