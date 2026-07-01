using Fray.Core.Operations;

namespace Fray.Core.Contexts;

/// <summary>
/// Models a manual-reset event (<see cref="ManualResetEventSlim"/>): once set,
/// all current and future waiters proceed until the event is reset.
/// </summary>
public sealed class ManualResetEventContext : Acquirable, IInterruptibleContext
{
    private readonly Dictionary<int, LockWaiter> _waiters = new();

    public bool IsSet { get; private set; }

    public ManualResetEventContext(bool initialState, object eventObject)
        : base(new ResourceInfo(ObjectIds.Of(eventObject), ResourceType.Event))
    {
        IsSet = initialState;
    }

    /// <summary>Returns true when the event is set; otherwise registers the waiter.</summary>
    public bool Wait(bool shouldBlock, bool canInterrupt, ThreadContext thread)
    {
        if (IsSet)
        {
            return true;
        }
        if (canInterrupt)
        {
            thread.CheckInterrupt();
        }
        if (shouldBlock)
        {
            _waiters[thread.Index] = new LockWaiter(canInterrupt, thread);
        }
        return false;
    }

    public void Set()
    {
        IsSet = true;
        foreach (var waiter in _waiters.Values.ToList())
        {
            waiter.Thread.PendingOperation = new ThreadResumeOperation(true);
            waiter.Thread.State = FrayThreadState.Runnable;
        }
        _waiters.Clear();
    }

    public void Reset() => IsSet = false;

    public bool UnblockThread(ThreadContext thread, InterruptionType type)
    {
        if (!_waiters.TryGetValue(thread.Index, out var waiter))
        {
            return false;
        }
        if ((waiter.CanInterrupt && type == InterruptionType.Interrupt) ||
            type is InterruptionType.Force or InterruptionType.Timeout)
        {
            waiter.Thread.PendingOperation = new ThreadResumeOperation(type != InterruptionType.Timeout);
            waiter.Thread.State = FrayThreadState.Runnable;
            _waiters.Remove(thread.Index);
        }
        return false;
    }
}
