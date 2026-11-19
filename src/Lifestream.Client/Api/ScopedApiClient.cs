using System.Collections.Concurrent;
using System.Reflection;
using Lifestream.Client.Common;

namespace Lifestream.Client.Api;

/// <summary>
/// Wraps the API client the screens use (SHELL-07). Every call made inside an <see cref="OperationScope"/> gets a token that is linked to the
/// scope's token and to a hard ceiling (the 120 s wake budget plus one attempt), whatever token the caller passed, so
/// <see cref="OperationRunner.Cancel"/> reaches <see cref="HttpClient"/> and the request never reaches the server. The runner is told when
/// the call starts and ends, which is what drives the "Waking up the server" state. A call outside any scope is a bug in a screen: with
/// <see cref="Strict"/> set (the tests) it throws; otherwise it is passed through unchanged. Methods marked <see cref="OutsideOperationAttribute"/>
/// (warm-up and sign-out, made by services) are never wrapped.
/// </summary>
public class ScopedApiClient : DispatchProxy
{
    private static readonly ConcurrentDictionary<Type, Func<Task, Action, Task>> Trackers = new();

    private ILifestreamApiClient? _inner;
    private TimeSpan _ceiling = TimeSpan.FromSeconds(140);
    private TimeProvider _clock = TimeProvider.System;

    /// <summary>When true a call made outside any operation throws instead of being passed through.</summary>
    public bool Strict { get; private set; }

    /// <summary>Wraps <paramref name="inner"/>. The ceiling is the longest any one call may take: the wake budget plus one attempt.</summary>
    public static ILifestreamApiClient Wrap(ILifestreamApiClient inner, WakePolicy? policy = null, bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var proxy = Create<ILifestreamApiClient, ScopedApiClient>();
        var scoped = (ScopedApiClient)(object)proxy;
        scoped._inner = inner;
        scoped.Strict = strict;
        foreach (var method in typeof(ILifestreamApiClient).GetMethods())
        {
            // Build the result trackers now, so the first call of each kind does not pay for reflection while a user waits.
            if (method.ReturnType.IsGenericType)
            {
                Trackers.GetOrAdd(method.ReturnType.GetGenericArguments()[0], MakeTracker);
            }
        }

        if (policy is not null)
        {
            scoped._ceiling = policy.Budget + policy.AttemptTimeout;
            scoped._clock = policy.Clock;
        }

        return proxy;
    }

    /// <summary>True when <paramref name="method"/> of the API may be called outside an operation.</summary>
    public static bool IsOutsideOperation(MethodInfo method) => method.GetCustomAttribute<OutsideOperationAttribute>() is not null;

    /// <summary>Throws when the current code is not inside an operation; the fakes of the tests call this so a call outside a runner cannot pass unseen.</summary>
    public static void RequireScope(string call)
    {
        if (OperationScope.Current is null)
        {
            throw new InvalidOperationException($"API call {call} was made outside an operation. Run it through the view model's OperationRunner (a Command, LoadAsync or Operations.RunAsync).");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The token sources are disposed by Done when the call's task ends.")]
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        var inner = _inner ?? throw new InvalidOperationException("The scoped client has no inner client.");
        var scope = OperationScope.Current;
        var tokenIndex = Array.FindIndex(targetMethod.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
        if (IsOutsideOperation(targetMethod) || tokenIndex < 0)
        {
            return Call(inner, targetMethod, args);
        }

        if (scope is null)
        {
            return Strict ? throw new InvalidOperationException($"API call {targetMethod.Name} was made outside an operation.") : Call(inner, targetMethod, args);
        }

        var ceiling = new CancellationTokenSource(_ceiling, _clock);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, (CancellationToken)args[tokenIndex]!, ceiling.Token);
        args[tokenIndex] = cts.Token;
        var started = scope.Runner.CallStarted();
        void Done()
        {
            scope.Runner.CallEnded(started);
            cts.Dispose();
            ceiling.Dispose();
        }

        Task task;
        try
        {
            task = (Task)Call(inner, targetMethod, args)!;
        }
        catch
        {
            Done();
            throw;
        }

        var resultType = targetMethod.ReturnType.IsGenericType ? targetMethod.ReturnType.GetGenericArguments()[0] : null;
        return resultType is null ? Track(task, Done) : Trackers.GetOrAdd(resultType, MakeTracker)(task, Done);
    }

    private static object? Call(ILifestreamApiClient inner, MethodInfo method, object?[] args)
    {
        try
        {
            return method.Invoke(inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static Func<Task, Action, Task> MakeTracker(Type resultType) =>
        typeof(ScopedApiClient).GetMethod(nameof(TrackOf), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType).CreateDelegate<Func<Task, Action, Task>>();

    private static async Task Track(Task task, Action done)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            done();
        }
    }

    private static async Task<T> TrackOf<T>(Task task, Action done)
    {
        try
        {
            return await ((Task<T>)task).ConfigureAwait(false);
        }
        finally
        {
            done();
        }
    }
}
