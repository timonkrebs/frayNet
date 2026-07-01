using Xunit;
using Xunit.Sdk;

namespace Fray.Xunit;

/// <summary>
/// Marks a test that runs under Fray's controlled concurrency exploration:
/// the test body is executed once per iteration with a different schedule
/// until a bug is found or the iteration budget is exhausted. On failure the
/// thrown <see cref="FrayBugFoundException"/> carries the engine's report.
///
/// The xUnit counterpart of the JVM implementation's
/// <c>@ConcurrencyTest</c> (fray-junit).
/// </summary>
[XunitTestCaseDiscoverer("Fray.Xunit.FrayFactDiscoverer", "Fray.Xunit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class FrayFactAttribute : FactAttribute
{
    /// <summary>Sentinel for "no seed configured" (attributes cannot hold int?).</summary>
    public const int NoSeed = int.MinValue;

    /// <summary>Maximum number of schedules to explore.</summary>
    public int Iterations { get; set; } = 100;

    /// <summary>Seed for reproducible exploration; random when left unset.</summary>
    public int Seed { get; set; } = NoSeed;

    public SchedulerKind Scheduler { get; set; } = SchedulerKind.Random;
}
