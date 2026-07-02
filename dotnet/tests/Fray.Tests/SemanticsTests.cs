using Xunit;

namespace Fray.Tests;

/// <summary>
/// Pins the fine-grained semantics of the controlled primitives: timeout
/// paths, reentrancy, interrupts, misuse exceptions, and wakeup counts.
/// </summary>
public class SemanticsTests
{
    // -----------------------------------------------------------------
    // Timeouts (under IgnoreTimedBlock, a timeout fires exactly when no
    // other thread can run — deterministically)
    // -----------------------------------------------------------------

    [Fact]
    public void TryLockReturnsFalseWhenHeld()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var mutex = new FrayLock();
            var observed = new FrayShared<bool>(true);
            mutex.Lock();
            var t = FrayThread.StartNew(() => observed.Value = mutex.TryLock());
            t.Join(); // t runs while the main thread holds the lock.
            Assert.False(observed.Value);
            mutex.Unlock();
            Assert.True(mutex.TryLock());
            mutex.Unlock();
        }, new FrayConfiguration { Iterations = 50, Seed = 7 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void TimedTryLockTimesOutWhenNeverReleased()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var mutex = new FrayLock();
            var observed = new FrayShared<bool>(true);
            mutex.Lock();
            var t = FrayThread.StartNew(() => observed.Value = mutex.TryLock(50));
            t.Join();
            Assert.False(observed.Value);
            mutex.Unlock();
        }, new FrayConfiguration { Iterations = 50, Seed = 7 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void MonitorWaitTimesOutWithoutSignal()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var gate = new object();
            FrayMonitor.Enter(gate);
            try
            {
                Assert.False(FrayMonitor.Wait(gate, 50));
            }
            finally
            {
                FrayMonitor.Exit(gate);
            }
        }, new FrayConfiguration { Iterations = 20, Seed = 7, AllowSpuriousWakeups = false });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void CountdownEventWaitTimesOutThenSucceeds()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var latch = new FrayCountdownEvent(1);
            Assert.False(latch.Wait(50));
            latch.Signal();
            Assert.True(latch.Wait(50));
        }, new FrayConfiguration { Iterations = 20, Seed = 7 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    // -----------------------------------------------------------------
    // Reentrancy
    // -----------------------------------------------------------------

    [Fact]
    public void LocksAndMonitorsAreReentrant()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var mutex = new FrayLock();
            mutex.Lock();
            mutex.Lock();
            Assert.True(mutex.TryLock());
            mutex.Unlock();
            mutex.Unlock();
            mutex.Unlock();

            var gate = new object();
            FrayMonitor.Enter(gate);
            FrayMonitor.Enter(gate);
            FrayMonitor.Exit(gate);
            FrayMonitor.Exit(gate);
        }, new FrayConfiguration { Iterations = 5, Seed = 7 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void ConditionAwaitRestoresReentrantHoldCount()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var mutex = new FrayLock();
            var ready = mutex.NewCondition();
            var go = new FrayShared<bool>(false);

            var signaler = FrayThread.StartNew(() =>
            {
                mutex.Lock();
                try
                {
                    go.Value = true;
                    ready.SignalAll();
                }
                finally
                {
                    mutex.Unlock();
                }
            });

            mutex.Lock();
            mutex.Lock(); // hold count 2
            try
            {
                while (!go.Value)
                {
                    ready.Await(); // must release fully and restore count 2
                }
            }
            finally
            {
                // Both unlocks must succeed; a wrong restored count would
                // throw SynchronizationLockException here.
                mutex.Unlock();
                mutex.Unlock();
            }
            signaler.Join();
            Assert.True(mutex.TryLock());
            mutex.Unlock();
        }, new FrayConfiguration { Iterations = 100, Seed = 11 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void WriterMayReenterAsReader()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var rwLock = new FrayReaderWriterLock();
            rwLock.EnterWriteLock();
            rwLock.EnterReadLock();
            rwLock.ExitReadLock();
            rwLock.ExitWriteLock();
        }, new FrayConfiguration { Iterations = 5, Seed = 7 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    // -----------------------------------------------------------------
    // Misuse
    // -----------------------------------------------------------------

    [Fact]
    public void UnlockWithoutHoldingIsReported()
    {
        var result = FrayTestRunner.Run(() => new FrayLock().Unlock(),
            new FrayConfiguration { Iterations = 1, Seed = 1 });

        Assert.IsType<SynchronizationLockException>(result.BugFound);
    }

    [Fact]
    public void MonitorExitWithoutHoldingIsReported()
    {
        var result = FrayTestRunner.Run(() => FrayMonitor.Exit(new object()),
            new FrayConfiguration { Iterations = 1, Seed = 1 });

        Assert.IsType<SynchronizationLockException>(result.BugFound);
    }

    [Fact]
    public void PulseWithoutHoldingIsReported()
    {
        var result = FrayTestRunner.Run(() => FrayMonitor.Pulse(new object()),
            new FrayConfiguration { Iterations = 1, Seed = 1 });

        Assert.IsType<SynchronizationLockException>(result.BugFound);
    }

    // -----------------------------------------------------------------
    // Wakeup semantics
    // -----------------------------------------------------------------

    [Fact]
    public void PulseWakesExactlyOneWaiter()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var gate = new object();
            var tokens = new FrayShared<int>(0);
            var consumed = new FrayShared<int>(0);
            var wakeups = new FrayAtomicInt32(0);

            void Consume()
            {
                FrayMonitor.Enter(gate);
                try
                {
                    while (tokens.Value == 0)
                    {
                        FrayMonitor.Wait(gate);
                        wakeups.IncrementAndGet();
                    }
                    tokens.Value = tokens.Value - 1;
                    consumed.Value = consumed.Value + 1;
                }
                finally
                {
                    FrayMonitor.Exit(gate);
                }
            }

            var w1 = FrayThread.StartNew(Consume);
            var w2 = FrayThread.StartNew(Consume);

            // Let both waiters block, then hand out a single token via Pulse.
            for (var i = 0; i < 20; i++)
            {
                FrayThread.Yield();
            }
            FrayMonitor.Enter(gate);
            tokens.Value = 1;
            FrayMonitor.Pulse(gate);
            FrayMonitor.Exit(gate);

            while (consumed.Value < 1)
            {
                FrayThread.Yield();
            }
            for (var i = 0; i < 25; i++)
            {
                FrayThread.Yield();
            }
            // A single Pulse must have woken at most one waiter.
            Assert.True(wakeups.Value <= 1, $"One Pulse produced {wakeups.Value} wakeups.");

            FrayMonitor.Enter(gate);
            tokens.Value = tokens.Value + 1;
            FrayMonitor.PulseAll(gate);
            FrayMonitor.Exit(gate);
            w1.Join();
            w2.Join();
            Assert.Equal(2, consumed.Value);
        }, new FrayConfiguration { Iterations = 100, Seed = 13, AllowSpuriousWakeups = false });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    // -----------------------------------------------------------------
    // Interrupts
    // -----------------------------------------------------------------

    [Fact]
    public void InterruptWakesMonitorWait()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var gate = new object();
            var caught = new FrayShared<bool>(false);
            var t = FrayThread.StartNew(() =>
            {
                try
                {
                    // The interrupt may land before Enter (which is itself
                    // interruptible, like .NET's Monitor.Enter) or during the
                    // wait (which reacquires the monitor before throwing).
                    FrayMonitor.Enter(gate);
                    try
                    {
                        while (true)
                        {
                            FrayMonitor.Wait(gate);
                        }
                    }
                    finally
                    {
                        FrayMonitor.Exit(gate);
                    }
                }
                catch (ThreadInterruptedException)
                {
                    caught.Value = true;
                }
            });

            t.Interrupt();
            t.Join();
            Assert.True(caught.Value);
        }, new FrayConfiguration { Iterations = 150, Seed = 17 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void InterruptWakesSleep()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var caught = new FrayShared<bool>(false);
            var t = FrayThread.StartNew(() =>
            {
                try
                {
                    FrayThread.Sleep(60_000);
                }
                catch (ThreadInterruptedException)
                {
                    caught.Value = true;
                }
            });

            t.Interrupt();
            t.Join();
            Assert.True(caught.Value);
        }, new FrayConfiguration { Iterations = 150, Seed = 19 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void InterruptWakesMonitorEnter()
    {
        // .NET semantics: Monitor.Enter (unlike Java's synchronized) responds
        // to interrupts.
        var result = FrayTestRunner.Run(() =>
        {
            var gate = new object();
            var caught = new FrayShared<bool>(false);
            FrayMonitor.Enter(gate);
            var t = FrayThread.StartNew(() =>
            {
                try
                {
                    FrayMonitor.Enter(gate);
                    FrayMonitor.Exit(gate);
                }
                catch (ThreadInterruptedException)
                {
                    caught.Value = true;
                }
            });

            t.Interrupt();
            FrayMonitor.Exit(gate);
            t.Join();
            Assert.True(caught.Value);
        }, new FrayConfiguration { Iterations = 150, Seed = 23 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    // -----------------------------------------------------------------
    // Semaphore permits
    // -----------------------------------------------------------------

    [Fact]
    public void MultiPermitAcquireAndTryAcquire()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var semaphore = new FraySemaphore(2);
            var observed = new FrayShared<bool>(true);
            semaphore.Acquire(2);
            var t = FrayThread.StartNew(() => observed.Value = semaphore.TryAcquire());
            t.Join();
            Assert.False(observed.Value);
            Assert.Equal(0, semaphore.AvailablePermits);
            semaphore.Release(2);
            Assert.Equal(2, semaphore.AvailablePermits);
        }, new FrayConfiguration { Iterations = 50, Seed = 29 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }
}
