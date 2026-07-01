using System.Text.Json.Serialization;
using Fray.Core.Operations;
using Fray.Core.Randomness;

namespace Fray.Core.Scheduling;

/// <summary>
/// Selectively Uniform Random Walk (SURW, https://dl.acm.org/doi/10.1145/3669940.3707214):
/// samples interleavings of "interesting" racing operations uniformly by
/// weighting threads with their (estimated) remaining execution lengths.
/// The first iteration observes execution lengths and racing call sites;
/// subsequent iterations use them as weights.
///
/// Mirrors <c>org.pastalab.fray.core.scheduler.SURWScheduler</c>. Deviations:
/// interestingness filtering of JDK-internal frames does not apply (memory
/// operations only originate from user code here), and the interesting-
/// operation selection is deterministic instead of unseeded shuffling.
///
/// Requires racing-operation call-site hashes
/// (<see cref="RacingOperation.ResolveStackTraceHashes"/>), which the runner
/// enables for this scheduler.
/// </summary>
public sealed class SurwScheduler : IScheduler
{
    public IRandomness Rand { get; }

    /// <summary>Estimated per-thread execution lengths from the previous iteration (thread index → weight).</summary>
    public Dictionary<int, int> ExecutionLengths { get; }

    /// <summary>Call-site hashes of the racing operations being sampled uniformly.</summary>
    public HashSet<int> InterestingOperations { get; }

    [JsonIgnore] private readonly Dictionary<int, int> _weight;
    [JsonIgnore] private readonly HashSet<int> _blocked = new();
    [JsonIgnore] private int _nextIntendedThread = -1;
    [JsonIgnore] private readonly HashSet<int> _createdThreads = new();
    [JsonIgnore] private readonly Dictionary<int, HashSet<int>> _childThreads = new();

    // First-trial bookkeeping used to construct the maps for later iterations:
    // resource id → thread index → call-site hashes, and observed lengths.
    [JsonIgnore] private readonly Dictionary<int, Dictionary<int, HashSet<int>>> _interestingObjectMap = new();
    [JsonIgnore] private readonly Dictionary<int, int> _threadExecutionLengthCache = new();

    public SurwScheduler() : this(new ControlledRandom(), new Dictionary<int, int>(), new HashSet<int>()) { }

    [JsonConstructor]
    public SurwScheduler(IRandomness rand, Dictionary<int, int> executionLengths, HashSet<int> interestingOperations)
    {
        Rand = rand;
        ExecutionLengths = executionLengths;
        InterestingOperations = interestingOperations;
        _weight = new Dictionary<int, int>(executionLengths);
    }

    public ThreadContext ScheduleNextOperation(IReadOnlyList<ThreadContext> threads,
        IReadOnlyCollection<ThreadContext> allThreads)
    {
        // Update weights when new threads appeared: a child inherits part of
        // its parent's remaining length.
        CheckNewThreads(threads);

        var filteredThreads = threads.Where(t => !_blocked.Contains(t.Index)).ToList();
        if (filteredThreads.Count == 0)
        {
            _nextIntendedThread = -1;
            _blocked.Clear();
            return ScheduleNextOperation(threads, allThreads);
        }

        var nextThread = filteredThreads[Rand.NextInt() % filteredThreads.Count];
        var nextOperation = nextThread.PendingOperation;

        if (nextOperation is RacingOperation racing && InterestingOperations.Contains(racing.StackTraceHash))
        {
            if (_weight.GetValueOrDefault(nextThread.Index) == 0)
            {
                _weight[nextThread.Index] = 1;
            }
            if (_nextIntendedThread == -1)
            {
                UpdateNextIntendedThread(filteredThreads);
            }
            if (nextThread.Index == _nextIntendedThread)
            {
                _weight[nextThread.Index] -= 1;
                _blocked.Clear();
                _nextIntendedThread = -1;
            }
            else
            {
                _blocked.Add(nextThread.Index);
                return ScheduleNextOperation(threads, allThreads);
            }
            if (ExecutionLengths.Count == 0)
            {
                _threadExecutionLengthCache[nextThread.Index] =
                    _threadExecutionLengthCache.GetValueOrDefault(nextThread.Index) + 1;
            }
        }

        // On the first trial, collect the interesting-operation candidates.
        if (InterestingOperations.Count == 0)
        {
            ConstructInterestingOperation(nextThread);
        }

        return nextThread;
    }

    private void UpdateNextIntendedThread(List<ThreadContext> threads)
    {
        var totalWeight = threads.Sum(t => _weight.GetValueOrDefault(t.Index));
        var selectedThreadWeight = (Rand.NextInt() % totalWeight) + 1;
        var currentWeight = 0;
        foreach (var thread in threads)
        {
            var threadWeight = _weight.GetValueOrDefault(thread.Index);
            currentWeight += threadWeight;
            if (currentWeight >= selectedThreadWeight && threadWeight != 0)
            {
                _nextIntendedThread = thread.Index;
                break;
            }
        }
    }

    private void CheckNewThreads(IReadOnlyList<ThreadContext> threads)
    {
        foreach (var thread in threads)
        {
            if (!_createdThreads.Add(thread.Index))
            {
                continue;
            }
            if (thread.PendingOperation is not ThreadStartOperation operation || operation.ParentIndex == -1)
            {
                continue;
            }
            var parentIndex = operation.ParentIndex;
            if (!_childThreads.TryGetValue(parentIndex, out var children))
            {
                children = new HashSet<int>();
                _childThreads[parentIndex] = children;
            }
            children.Add(thread.Index);
            var childWeight = _weight.GetValueOrDefault(thread.Index);
            var parentWeight = _weight.GetValueOrDefault(parentIndex);
            if (_nextIntendedThread == parentIndex)
            {
                var randomWeight = Rand.NextInt() % Math.Max(parentWeight, 1) + 1;
                if (childWeight > randomWeight)
                {
                    _nextIntendedThread = thread.Index;
                }
            }
            _weight[parentIndex] = Math.Max(parentWeight - childWeight, 0);
        }
    }

    private void ConstructInterestingOperation(ThreadContext thread)
    {
        if (thread.PendingOperation is not MemoryOperation operation)
        {
            return;
        }
        if (!_interestingObjectMap.TryGetValue(operation.Resource, out var byThread))
        {
            byThread = new Dictionary<int, HashSet<int>>();
            _interestingObjectMap[operation.Resource] = byThread;
        }
        if (!byThread.TryGetValue(thread.Index, out var sites))
        {
            sites = new HashSet<int>();
            byThread[thread.Index] = sites;
        }
        sites.Add(operation.StackTraceHash);
    }

    private Dictionary<int, int> BuildThreadWeights()
    {
        var threadWeights = new Dictionary<int, int>();
        if (_threadExecutionLengthCache.Count > 0)
        {
            foreach (var thread in _createdThreads)
            {
                BuildThreadWeightsRecursive(thread, threadWeights);
            }
        }
        return threadWeights;
    }

    private int BuildThreadWeightsRecursive(int threadIndex, Dictionary<int, int> threadWeights)
    {
        if (!threadWeights.TryGetValue(threadIndex, out var weight))
        {
            weight = _threadExecutionLengthCache.GetValueOrDefault(threadIndex);
            if (_childThreads.TryGetValue(threadIndex, out var children))
            {
                weight += children.Sum(child => BuildThreadWeightsRecursive(child, threadWeights));
            }
            threadWeights[threadIndex] = weight;
        }
        return weight;
    }

    private HashSet<int> BuildInterestingOperations()
    {
        // Resources touched by more than one thread are the racing candidates.
        // Deterministic selection (ordered by resource id) instead of the
        // JVM implementation's unseeded shuffle.
        return _interestingObjectMap
            .Where(entry => entry.Value.Count > 1)
            .OrderBy(entry => entry.Key)
            .Take(20)
            .SelectMany(entry => entry.Value.Values.SelectMany(sites => sites))
            .ToHashSet();
    }

    public IScheduler NextIteration(IRandomness randomness) =>
        new SurwScheduler(
            randomness,
            ExecutionLengths.Count > 0 ? ExecutionLengths : BuildThreadWeights(),
            InterestingOperations.Count > 0 ? InterestingOperations : BuildInterestingOperations());
}
