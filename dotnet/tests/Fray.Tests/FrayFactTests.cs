using Fray.Xunit;
using Xunit;

namespace Fray.Tests;

/// <summary>
/// Exercises the <c>[FrayFact]</c> xUnit integration: each test body is
/// explored across many schedules; a found bug would fail the test with
/// <see cref="FrayBugFoundException"/>. State must be created inside the
/// body — the same instance is reused for every iteration.
/// </summary>
public class FrayFactTests
{
    [FrayFact(Iterations = 100, Seed = 7)]
    public void LockedCounterIsAlwaysConsistent()
    {
        var counter = new FrayShared<int>(0);
        var mutex = new FrayLock();

        void Increment() => mutex.WithLock(() => counter.Value = counter.Value + 1);

        var t1 = FrayThread.StartNew(Increment);
        var t2 = FrayThread.StartNew(Increment);
        t1.Join();
        t2.Join();
        Assert.Equal(2, counter.Value);
    }

    [FrayFact(Iterations = 75, Seed = 11, Scheduler = SchedulerKind.Pos)]
    public void ConditionHandoffDeliversValue()
    {
        var mutex = new FrayLock();
        var ready = mutex.NewCondition();
        var slot = new FrayShared<int>(0);
        var hasValue = new FrayShared<bool>(false);

        var consumer = FrayThread.StartNew(() =>
        {
            mutex.Lock();
            try
            {
                while (!hasValue.Value)
                {
                    ready.Await();
                }
                Assert.Equal(42, slot.Value);
            }
            finally
            {
                mutex.Unlock();
            }
        });

        mutex.Lock();
        try
        {
            slot.Value = 42;
            hasValue.Value = true;
            ready.SignalAll();
        }
        finally
        {
            mutex.Unlock();
        }
        consumer.Join();
    }

    [FrayFact]
    public void DefaultConfigurationRunsThreads()
    {
        var done = new FrayShared<bool>(false);
        var worker = FrayThread.StartNew(() => done.Value = true);
        worker.Join();
        Assert.True(done.Value);
    }
}
