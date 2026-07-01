using Fray.Core.Randomness;
using Fray.Core.Scheduling;
using Xunit;

namespace Fray.Tests;

public class SchedulerTests
{
    [Fact]
    public void SurwSchedulerRoundTripsThroughJson()
    {
        var scheduler = new SurwScheduler(new ControlledRandom(1),
            new Dictionary<int, int> { [0] = 3, [1] = 5 },
            new HashSet<int> { 42, -7 });

        var json = SchedulerSerialization.ToJson(scheduler);
        var restored = SchedulerSerialization.FromJson(json);

        var surw = Assert.IsType<SurwScheduler>(restored);
        Assert.Equal(scheduler.ExecutionLengths, surw.ExecutionLengths);
        Assert.Equal(scheduler.InterestingOperations, surw.InterestingOperations);
    }

    [Fact]
    public void SurwLearnsWeightsAcrossIterations()
    {
        // After the observation iteration, the next-iteration scheduler must
        // carry non-empty execution lengths and interesting operations.
        var result = FrayTestRunner.Run(() =>
        {
            var counter = new FrayShared<int>(0);
            var t1 = FrayThread.StartNew(() => counter.Value = counter.Value + 1);
            var t2 = FrayThread.StartNew(() => counter.Value = counter.Value + 1);
            t1.Join();
            t2.Join();
        }, new FrayConfiguration
        {
            Scheduler = SchedulerKind.Surw,
            Iterations = 30,
            Seed = 5,
        });

        Assert.True(result.BugFound == null, result.ErrorReport);
    }
}
