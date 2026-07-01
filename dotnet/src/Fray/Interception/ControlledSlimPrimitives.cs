namespace Fray.Interception;

/// <summary>
/// Replacements for the synchronous surface of <see cref="SemaphoreSlim"/>,
/// used by the IL rewriter. The model state is created from the instance's
/// real count on first controlled contact. <c>WaitAsync</c> is not redirected:
/// awaiting its task fails fast like any uncontrolled task.
/// </summary>
public static class ControlledSemaphoreSlim
{
    public static void Wait(SemaphoreSlim semaphore)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            semaphore.Wait();
            return;
        }
        runContext.SemaphoreAcquire(semaphore, semaphore.CurrentCount, permits: 1, shouldBlock: true,
            canInterrupt: true, Core.Operations.BlockedOperation.NotTimed);
    }

    public static bool Wait(SemaphoreSlim semaphore, int millisecondsTimeout)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            return semaphore.Wait(millisecondsTimeout);
        }
        return runContext.SemaphoreAcquire(semaphore, semaphore.CurrentCount, permits: 1, shouldBlock: true,
            canInterrupt: true, runContext.DeadlineFor(millisecondsTimeout));
    }

    public static int Release(SemaphoreSlim semaphore) => Release(semaphore, 1);

    public static int Release(SemaphoreSlim semaphore, int releaseCount)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            return semaphore.Release(releaseCount);
        }
        var previousCount = runContext.SemaphorePermits(semaphore, semaphore.CurrentCount);
        runContext.SemaphoreRelease(semaphore, semaphore.CurrentCount, releaseCount);
        return previousCount;
    }

    public static int CurrentCount(SemaphoreSlim semaphore)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            return semaphore.CurrentCount;
        }
        return runContext.SemaphorePermits(semaphore, semaphore.CurrentCount);
    }
}

/// <summary>
/// Replacements for <see cref="ManualResetEventSlim"/>, used by the IL
/// rewriter. The model state is created from the instance's real
/// <c>IsSet</c> on first controlled contact.
/// </summary>
public static class ControlledManualResetEventSlim
{
    public static void Wait(ManualResetEventSlim resetEvent)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            resetEvent.Wait();
            return;
        }
        runContext.EventWait(resetEvent, resetEvent.IsSet, Core.Operations.BlockedOperation.NotTimed);
    }

    public static bool Wait(ManualResetEventSlim resetEvent, int millisecondsTimeout)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            return resetEvent.Wait(millisecondsTimeout);
        }
        return runContext.EventWait(resetEvent, resetEvent.IsSet, runContext.DeadlineFor(millisecondsTimeout));
    }

    public static void Set(ManualResetEventSlim resetEvent)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            resetEvent.Set();
            return;
        }
        runContext.EventSet(resetEvent, resetEvent.IsSet);
    }

    public static void Reset(ManualResetEventSlim resetEvent)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            resetEvent.Reset();
            return;
        }
        runContext.EventReset(resetEvent, resetEvent.IsSet);
    }

    public static bool IsSet(ManualResetEventSlim resetEvent)
    {
        var runContext = FrayRuntime.ControlledContext();
        if (runContext == null)
        {
            return resetEvent.IsSet;
        }
        return runContext.EventIsSet(resetEvent, resetEvent.IsSet);
    }
}
