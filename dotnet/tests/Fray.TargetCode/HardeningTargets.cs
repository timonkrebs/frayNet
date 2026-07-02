namespace Fray.TargetCode;

/// <summary>
/// Plain code exercising less-happy paths under rewriting: exceptions
/// unwinding through <c>lock</c>, interrupting a lock-blocked thread, and
/// timed joins.
/// </summary>
public static class HardeningTargets
{
    public static void ExceptionInsideLockReleasesIt()
    {
        var gate = new object();
        var secondEntered = false;

        var thrower = new Thread(() =>
        {
            try
            {
                lock (gate)
                {
                    throw new InvalidOperationException("inside lock");
                }
            }
            catch (InvalidOperationException)
            {
                // Swallowed: the lock must have been released by the unwind.
            }
        });
        var second = new Thread(() =>
        {
            lock (gate)
            {
                secondEntered = true;
            }
        });

        thrower.Start();
        second.Start();
        thrower.Join();
        second.Join();
        if (!secondEntered)
        {
            throw new InvalidOperationException("Second thread never got the lock.");
        }
    }

    public static void InterruptWakesLockStatement()
    {
        var gate = new object();
        var caught = false;

        Thread blocked;
        lock (gate)
        {
            blocked = new Thread(() =>
            {
                try
                {
                    lock (gate)
                    {
                    }
                }
                catch (ThreadInterruptedException)
                {
                    caught = true;
                }
            });
            blocked.Start();
            blocked.Interrupt();
        }
        blocked.Join();
        if (!caught)
        {
            throw new InvalidOperationException("Interrupt did not wake the lock statement.");
        }
    }

    public static void TimedJoinTimesOutThenSucceeds()
    {
        var gate = new object();
        var go = false;

        var waiter = new Thread(() =>
        {
            lock (gate)
            {
                while (!go)
                {
                    Monitor.Wait(gate);
                }
            }
        });
        waiter.Start();

        if (waiter.Join(50))
        {
            throw new InvalidOperationException("Join should have timed out.");
        }
        lock (gate)
        {
            go = true;
            Monitor.PulseAll(gate);
        }
        waiter.Join();
    }
}
