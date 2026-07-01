using System.Runtime.CompilerServices;
using Fray.Core;

namespace Fray.Interception;

/// <summary>
/// Replacements for <c>async ValueTask</c> machinery
/// (<see cref="AsyncValueTaskMethodBuilder"/>) and <see cref="ValueTask{TResult}"/>
/// observation, used by the IL rewriter.
///
/// Under control, the builder is forced onto its Task-backed slow path by
/// materializing <c>builder.Task</c> through the ref (the value-task builder
/// wraps an <see cref="AsyncTaskMethodBuilder"/> internally), after which the
/// method participates in exactly the same promise machinery as
/// <c>async Task</c> methods. Outside a Fray run the allocation-free fast
/// path is untouched.
/// </summary>
public static class ControlledValueTask
{
    // -----------------------------------------------------------------
    // AsyncValueTaskMethodBuilder (async ValueTask methods)
    // -----------------------------------------------------------------

    public static ValueTask GetTask(ref AsyncValueTaskMethodBuilder builder)
    {
        if (FrayRuntime.ControlledContext() == null)
        {
            return builder.Task;
        }
        var valueTask = builder.Task; // Forces the Task-backed path in place.
        if (ControlledAsync.GetValueTaskBacking(valueTask) is Task task)
        {
            ControlledTask.EnsurePromiseEntry(task);
        }
        return valueTask;
    }

    public static void SetResult(ref AsyncValueTaskMethodBuilder builder)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.SetResult();
            return;
        }
        var task = BackingTask(builder.Task);
        builder.SetResult();
        ControlledTask.CompletePromise(runContext, task, null);
    }

    public static void SetException(ref AsyncValueTaskMethodBuilder builder, Exception exception)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.SetException(exception);
            return;
        }
        var task = BackingTask(builder.Task);
        builder.SetException(exception);
        ControlledTask.CompletePromise(runContext, task, exception);
    }

    public static void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref AsyncValueTaskMethodBuilder builder, ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        // Materialize the backing task in place BEFORE the state machine is
        // copied, so every copy's builder shares it.
        ControlledTask.EnsurePromiseEntry(BackingTask(builder.Task));
        ControlledAsync.Suspend(runContext, ref awaiter, stateMachine);
    }

    public static void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref AsyncValueTaskMethodBuilder builder, ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.AwaitOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        ControlledTask.EnsurePromiseEntry(BackingTask(builder.Task));
        ControlledAsync.Suspend(runContext, ref awaiter, stateMachine);
    }

    private static Task BackingTask(ValueTask valueTask) =>
        ControlledAsync.GetValueTaskBacking(valueTask) as Task
        ?? throw new FrayInternalException("Controlled async ValueTask builder is not Task-backed.");

    // -----------------------------------------------------------------
    // AsyncValueTaskMethodBuilder<TResult> (async ValueTask<TResult> methods)
    // -----------------------------------------------------------------

    public static ValueTask<TResult> GetTask<TResult>(ref AsyncValueTaskMethodBuilder<TResult> builder)
    {
        if (FrayRuntime.ControlledContext() == null)
        {
            return builder.Task;
        }
        var valueTask = builder.Task;
        if (ControlledAsync.GetValueTaskBacking(valueTask) is Task task)
        {
            ControlledTask.EnsurePromiseEntry(task);
        }
        return valueTask;
    }

    public static void SetResult<TResult>(ref AsyncValueTaskMethodBuilder<TResult> builder, TResult result)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.SetResult(result);
            return;
        }
        var task = BackingTask(builder.Task);
        builder.SetResult(result);
        ControlledTask.CompletePromise(runContext, task, null);
    }

    public static void SetException<TResult>(ref AsyncValueTaskMethodBuilder<TResult> builder, Exception exception)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.SetException(exception);
            return;
        }
        var task = BackingTask(builder.Task);
        builder.SetException(exception);
        ControlledTask.CompletePromise(runContext, task, exception);
    }

    public static void AwaitUnsafeOnCompleted<TResult, TAwaiter, TStateMachine>(
        ref AsyncValueTaskMethodBuilder<TResult> builder, ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        ControlledTask.EnsurePromiseEntry(BackingTask<TResult>(builder.Task));
        ControlledAsync.Suspend(runContext, ref awaiter, stateMachine);
    }

    public static void AwaitOnCompleted<TResult, TAwaiter, TStateMachine>(
        ref AsyncValueTaskMethodBuilder<TResult> builder, ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            builder.AwaitOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        ControlledTask.EnsurePromiseEntry(BackingTask<TResult>(builder.Task));
        ControlledAsync.Suspend(runContext, ref awaiter, stateMachine);
    }

    private static Task BackingTask<TResult>(ValueTask<TResult> valueTask) =>
        ControlledAsync.GetValueTaskBacking(valueTask) as Task
        ?? throw new FrayInternalException("Controlled async ValueTask builder is not Task-backed.");

    // -----------------------------------------------------------------
    // ValueTask<TResult> observation
    // -----------------------------------------------------------------

    public static TResult Result<TResult>(ref ValueTask<TResult> valueTask)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null || valueTask.IsCompleted)
        {
            return valueTask.Result;
        }
        if (ControlledAsync.GetValueTaskBacking(valueTask) is Task task)
        {
            // Silent join: the real Result then surfaces the original
            // exception (ValueTask semantics, no AggregateException).
            ControlledTask.JoinSilently(runContext, task);
            return valueTask.Result;
        }
        throw new NotSupportedException(
            "Fray: cannot wait for a ValueTask that is not backed by a controlled Task.");
    }
}
