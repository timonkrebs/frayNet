using Xunit;

namespace Fray.Tests;

public class CoverageTests
{
    // The reader's branch depends on a racy flag: different schedules execute
    // different write sites, i.e. different timelines.
    private static void BranchingBody()
    {
        var flag = new FrayShared<bool>(false);
        var value = new FrayShared<int>(0);
        var setter = FrayThread.StartNew(() => flag.Value = true);
        var reader = FrayThread.StartNew(() =>
        {
            if (flag.Value)
            {
                value.Value = 1;
            }
            else
            {
                value.Value = 2;
            }
        });
        setter.Join();
        reader.Join();
    }

    [Fact]
    public void RandomExplorationCoversMultipleTimelines()
    {
        var result = FrayTestRunner.Run(BranchingBody, new FrayConfiguration
        {
            Iterations = 60,
            Seed = 13,
            TrackTimelineCoverage = true,
        });

        Assert.True(result.BugFound == null, result.ErrorReport);
        Assert.NotNull(result.CoveredTimelines);
        Assert.True(result.CoveredTimelines >= 2,
            $"Expected exploration to cover at least 2 timelines, got {result.CoveredTimelines}.");
    }

    [Fact]
    public void FifoSchedulerCoversExactlyOneTimeline()
    {
        var result = FrayTestRunner.Run(BranchingBody, new FrayConfiguration
        {
            Scheduler = SchedulerKind.Fifo,
            Iterations = 20,
            TrackTimelineCoverage = true,
            // Spurious wakeups draw fresh randomness per iteration and a
            // repeated wait shows up in the timeline; disable them so FIFO
            // executions are fully identical.
            AllowSpuriousWakeups = false,
        });

        Assert.True(result.BugFound == null, result.ErrorReport);
        Assert.Equal(1, result.CoveredTimelines);
    }

    [Fact]
    public void CoverageIsNullWhenNotTracked()
    {
        var result = FrayTestRunner.Run(BranchingBody, new FrayConfiguration
        {
            Iterations = 5,
            Seed = 13,
        });

        Assert.True(result.BugFound == null, result.ErrorReport);
        Assert.Null(result.CoveredTimelines);
    }
}
