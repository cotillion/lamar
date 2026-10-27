using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImTools;
using Lamar.IoC;
using Lamar.IoC.Instances;
using Lamar.IoC.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Lamar.Testing.Bugs;

public class Bug_lambda_resolver_publication
{
    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void resolver_has_its_cache_identity_before_it_can_be_published(ServiceLifetime lifetime)
    {
        using var container = Container.For(_ => { });
        var instance = new InspectableLambda(lifetime) { Name = "named-lambda", Hash = 12345 };

        // Exercise the construction boundary without publishing the resolver. This catches the
        // initialization-order bug deterministically, without depending on a narrow scheduling window.
        var resolver = instance.CreateUnpublishedResolver(container);

        resolver.Hash.ShouldBe(instance.Hash);
        resolver.Name.ShouldBe(instance.Name);
        resolver.Resolve(container).ShouldBeOfType<Service>();
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public async Task concurrent_resolution_paths_share_the_correct_cache_entry(ServiceLifetime lifetime)
    {
        using var container = Container.For(_ => { });
        using var scope = container.GetNestedContainer();
        using var ready = new CountdownEvent(8);
        using var proceed = new ManualResetEventSlim();
        var builds = 0;
        var instance = new LambdaInstance<IServiceProvider, Service>(typeof(Service), _ =>
        {
            Interlocked.Increment(ref builds);
            return new Service();
        }, lifetime) { Name = "named-lambda", Hash = 12345 };

        var resolvingScope = (Scope)scope;
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            proceed.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            return i % 2 == 0
                ? instance.ToResolver(resolvingScope)(resolvingScope)
                : instance.Resolve(resolvingScope);
        }, TaskCreationOptions.LongRunning)).ToArray();

        try
        {
            ready.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
        finally
        {
            proceed.Set();
        }

        var services = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var service in services) service.ShouldBeSameAs(services[0]);
        builds.ShouldBe(1);

        var holdingScope = lifetime == ServiceLifetime.Singleton ? container : resolvingScope;
        holdingScope.Services.TryFind(instance.Hash, out var cached).ShouldBeTrue();
        cached.ShouldBeSameAs(services[0]);
        holdingScope.Services.TryFind(0, out _).ShouldBeFalse();
    }

    private class InspectableLambda : LambdaInstance<IServiceProvider, Service>
    {
        public InspectableLambda(ServiceLifetime lifetime) : base(typeof(Service), _ => new Service(), lifetime) { }

        public IResolver CreateUnpublishedResolver(Scope scope) => buildResolver(scope);
    }

    public class Service { }
}
