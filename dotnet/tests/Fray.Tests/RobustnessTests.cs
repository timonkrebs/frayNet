using Fray.Core;
using Xunit;

namespace Fray.Tests;

/// <summary>
/// Harness robustness: livelocks abort cleanly, explore mode keeps going,
/// ignored exceptions stay ignored, and the engine survives thread fan-out.
/// </summary>
public class RobustnessTests
{
    [Fact]
    public void LivelockAbortsIterationsWithoutHanging()
    {
        var start = Environment.TickCount64;
        var result = FrayTestRunner.Run(() =>
        {
            while (true)
            {
                FrayThread.Yield();
            }
        }, new FrayConfiguration { Iterations = 3, Seed = 7, MaxScheduledSteps = 300 });

        // A liveness violation aborts the iteration but is not a bug.
        Assert.True(result.BugFound == null, result.ErrorReport);
        Assert.Equal(3, result.Iterations);
        Assert.True(Environment.TickCount64 - start < 30_000, "Livelock handling consumed wall time.");
    }

    [Fact]
    public void SpinningChildLivelockAbortsCleanly()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var t = FrayThread.StartNew(() =>
            {
                while (true)
                {
                    FrayThread.Yield();
                }
            });
            t.Join();
        }, new FrayConfiguration { Iterations = 3, Seed = 7, MaxScheduledSteps = 300 });

        Assert.True(result.BugFound == null, result.ErrorReport);
        Assert.Equal(3, result.Iterations);
    }

    [Fact]
    public void ExploreModeRunsAllIterationsAndKeepsFirstBug()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var counter = new FrayShared<int>(0);
            var t1 = FrayThread.StartNew(() => counter.Value = counter.Value + 1);
            var t2 = FrayThread.StartNew(() => counter.Value = counter.Value + 1);
            t1.Join();
            t2.Join();
            if (counter.Value != 2)
            {
                throw new InvalidOperationException("Lost update");
            }
        }, new FrayConfiguration { Iterations = 25, Seed = 42, ExploreMode = true });

        Assert.NotNull(result.BugFound);
        Assert.Contains("Lost update", result.BugFound!.Message);
        Assert.Equal(25, result.Iterations);
    }

    [Fact]
    public void IgnoredUnhandledExceptionsAreNotBugs()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var t = FrayThread.StartNew(() => throw new InvalidOperationException("ignored"));
            t.Join();
        }, new FrayConfiguration { Iterations = 20, Seed = 7, IgnoreUnhandledExceptions = true });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void UnhandledChildExceptionIsABugByDefault()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var t = FrayThread.StartNew(() => throw new InvalidOperationException("child-boom"));
            t.Join();
        }, new FrayConfiguration { Iterations = 5, Seed = 7 });

        Assert.NotNull(result.BugFound);
        Assert.Contains("child-boom", result.BugFound!.Message);
    }

    [Fact]
    public void ManyThreadsFindTheLostUpdate()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var counter = new FrayShared<int>(0);
            var threads = new FrayThread[6];
            for (var i = 0; i < threads.Length; i++)
            {
                threads[i] = FrayThread.StartNew(() => counter.Value = counter.Value + 1);
            }
            foreach (var thread in threads)
            {
                thread.Join();
            }
            if (counter.Value != threads.Length)
            {
                throw new InvalidOperationException($"Lost update: {counter.Value} of {threads.Length}");
            }
        }, new FrayConfiguration { Iterations = 200, Seed = 42 });

        Assert.NotNull(result.BugFound);
        Assert.Contains("Lost update", result.BugFound!.Message);
    }

    [Fact]
    public void ManyThreadsWithLockStayConsistent()
    {
        var result = FrayTestRunner.Run(() =>
        {
            var counter = new FrayShared<int>(0);
            var mutex = new FrayLock();
            var threads = new FrayThread[6];
            for (var i = 0; i < threads.Length; i++)
            {
                threads[i] = FrayThread.StartNew(() =>
                {
                    for (var j = 0; j < 2; j++)
                    {
                        mutex.WithLock(() => counter.Value = counter.Value + 1);
                    }
                });
            }
            foreach (var thread in threads)
            {
                thread.Join();
            }
            Assert.Equal(12, counter.Value);
        }, new FrayConfiguration { Iterations = 60, Seed = 42 });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }

    [Fact]
    public void DeadlockException_IsTargetTerminate_SoUserCatchAllDoesNotMaskIt()
    {
        // A user catch (Exception) around a deadlocking acquire swallows the
        // unwind, but the bug stays reported.
        var result = FrayTestRunner.Run(() =>
        {
            var lockA = new FrayLock();
            var lockB = new FrayLock();
            var t1 = FrayThread.StartNew(() =>
            {
                try
                {
                    lockA.Lock();
                    lockB.Lock();
                    lockB.Unlock();
                    lockA.Unlock();
                }
                catch (Exception)
                {
                    // Swallow everything.
                }
            });
            var t2 = FrayThread.StartNew(() =>
            {
                try
                {
                    lockB.Lock();
                    lockA.Lock();
                    lockA.Unlock();
                    lockB.Unlock();
                }
                catch (Exception)
                {
                }
            });
            t1.Join();
            t2.Join();
        }, new FrayConfiguration { Iterations = 1000, Seed = 3 });

        Assert.IsType<DeadlockException>(result.BugFound);
    }
}
