using Fray.Core.Operations;

namespace Fray.Core.Observers;

/// <summary>
/// Counts distinct thread-ordering behaviors ("timelines") observed across
/// iterations: per thread, the set of racing-operation call sites executed
/// and the ordered pairs among them. Two iterations with the same abstract
/// state exercised the same behavior.
///
/// Mirrors <c>org.pastalab.fray.core.observers.ThreadOrderingCoverage</c>.
/// One instance is shared across all iterations of a run.
/// </summary>
public sealed class ThreadOrderingCoverage : IScheduleObserver
{
    private readonly HashSet<int> _coveredTimelines = new();
    private ThreadTimeline _currentTimeline = new();

    public int Coverage => _coveredTimelines.Count;

    public void OnExecutionStart() => _currentTimeline = new ThreadTimeline();

    public void OnNewSchedule(IReadOnlyList<ThreadContext> allThreads, ThreadContext scheduled)
    {
        if (scheduled.PendingOperation is RacingOperation operation)
        {
            _currentTimeline.RecordEvent(scheduled.Index, operation);
        }
    }

    public void OnContextSwitch(ThreadContext current, ThreadContext next) { }

    public void OnReportError(Exception exception) { }

    public void OnExecutionDone(Exception? bugFound) => _coveredTimelines.Add(_currentTimeline.AbstractState());
}

internal sealed class ThreadTimeline
{
    private readonly Dictionary<int, HashSet<string>> _perThreadEvents = new();
    private readonly Dictionary<int, HashSet<long>> _perThreadPairs = new();

    public void RecordEvent(int threadIndex, RacingOperation operation)
    {
        var eventId = $"{operation.GetType().Name}:{operation.Type}:{operation.StackTraceHash}";

        if (!_perThreadEvents.TryGetValue(threadIndex, out var events))
        {
            events = new HashSet<string>();
            _perThreadEvents[threadIndex] = events;
        }
        if (!_perThreadPairs.TryGetValue(threadIndex, out var pairs))
        {
            pairs = new HashSet<long>();
            _perThreadPairs[threadIndex] = pairs;
        }
        foreach (var previous in events)
        {
            pairs.Add(PairHash(previous, eventId));
        }
        events.Add(eventId);
    }

    public int AbstractState()
    {
        unchecked
        {
            var hash = 0;
            foreach (var (threadIndex, pairs) in _perThreadPairs.OrderBy(e => e.Key))
            {
                hash = hash * 31 + threadIndex;
                foreach (var pair in pairs.OrderBy(p => p))
                {
                    hash = hash * 31 + pair.GetHashCode();
                }
            }
            foreach (var (threadIndex, events) in _perThreadEvents.OrderBy(e => e.Key))
            {
                hash = hash * 31 + threadIndex;
                foreach (var eventId in events.OrderBy(e => e, StringComparer.Ordinal))
                {
                    hash = hash * 31 + eventId.GetHashCode();
                }
            }
            return hash;
        }
    }

    private static long PairHash(string a, string b) => a.GetHashCode() * 31L + b.GetHashCode();
}
