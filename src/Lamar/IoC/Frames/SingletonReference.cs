using System;
using System.Linq.Expressions;
using System.Threading;
using Lamar.IoC.Instances;

namespace Lamar.IoC.Frames;

/// <summary>
///     A singleton that a generated resolver injects, resolved the first time the resolver runs instead of when the
///     resolver is generated
/// </summary>
/// <remarks>
///     Resolvers are generated while holding the service graph lock. Building a singleton runs user code under the
///     singleton's own lock, and that code may resolve a type that needs the service graph lock, so building a singleton
///     while generating a resolver could deadlock with another thread that was building the same singleton. The
///     singletons a resolver injects are built before it's generated (see <see cref="Instance.BuildInjectedSingletons" />)
///     and inlined as constants; this is only for one that wasn't, and resolves it through its instance, which builds it
///     only once.
/// </remarks>
internal static class SingletonReference
{
    /// <summary>
    ///     An expression for the singleton: <c>reference.Value ?? reference.Resolve()</c>
    /// </summary>
    public static Expression For(Instance instance, Scope rootScope)
    {
        var serviceType = instance.ServiceType;

        // A value type is held boxed
        var heldType = serviceType.IsValueType ? typeof(object) : serviceType;
        var referenceType = typeof(SingletonReference<>).MakeGenericType(heldType);
        var reference = Expression.Constant(Activator.CreateInstance(referenceType, instance, rootScope));

        Expression value = Expression.Coalesce(
            Expression.Field(reference, nameof(SingletonReference<object>.Value)),
            Expression.Call(reference, nameof(SingletonReference<object>.Resolve), Type.EmptyTypes));

        return heldType == serviceType ? value : Expression.Convert(value, serviceType);
    }
}

internal sealed class SingletonReference<T> where T : class
{
    private readonly Instance _instance;
    private readonly Scope _rootScope;
    private int _initialized;

    // Read without a barrier by the resolvers: it's only written once the singleton has been built, and a resolver that
    // doesn't see it yet resolves the singleton through the instance, which returns the one it built
    public T Value;

    public SingletonReference(Instance instance, Scope rootScope)
    {
        _instance = instance;
        _rootScope = rootScope;
    }

    public T Resolve()
    {
        // Null cannot take the expression's coalesce fast path, but is still a cached result.
        if (Volatile.Read(ref _initialized) != 0)
        {
            return Value;
        }

        var value = (T)_instance.QuickResolve(_rootScope);
        Volatile.Write(ref Value, value);
        Volatile.Write(ref _initialized, 1);
        return value;
    }
}
