using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Fray.Xunit;

public class FrayFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnosticMessageSink;

    public FrayFactDiscoverer(IMessageSink diagnosticMessageSink) =>
        _diagnosticMessageSink = diagnosticMessageSink;

    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        yield return new FrayTestCase(_diagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod,
            factAttribute.GetNamedArgument<int>(nameof(FrayFactAttribute.Iterations)),
            factAttribute.GetNamedArgument<int>(nameof(FrayFactAttribute.Seed)),
            factAttribute.GetNamedArgument<SchedulerKind>(nameof(FrayFactAttribute.Scheduler)));
    }
}

public class FrayTestCase : XunitTestCase
{
    private int _iterations;
    private int _seed;
    private SchedulerKind _scheduler;

    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public FrayTestCase() { }

    public FrayTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod,
        int iterations, int seed, SchedulerKind scheduler)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
        _iterations = iterations;
        _seed = seed;
        _scheduler = scheduler;
    }

    public override void Serialize(IXunitSerializationInfo data)
    {
        base.Serialize(data);
        data.AddValue("FrayIterations", _iterations);
        data.AddValue("FraySeed", _seed);
        data.AddValue("FrayScheduler", (int)_scheduler);
    }

    public override void Deserialize(IXunitSerializationInfo data)
    {
        base.Deserialize(data);
        _iterations = data.GetValue<int>("FrayIterations");
        _seed = data.GetValue<int>("FraySeed");
        _scheduler = (SchedulerKind)data.GetValue<int>("FrayScheduler");
    }

    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus,
        object[] constructorArguments, ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource) =>
        new FrayTestCaseRunner(this, DisplayName, SkipReason, constructorArguments, TestMethodArguments,
            messageBus, aggregator, cancellationTokenSource, _iterations, _seed, _scheduler).RunAsync();
}

public class FrayTestCaseRunner : XunitTestCaseRunner
{
    private readonly int _iterations;
    private readonly int _seed;
    private readonly SchedulerKind _scheduler;

    public FrayTestCaseRunner(IXunitTestCase testCase, string displayName, string? skipReason,
        object[] constructorArguments, object[] testMethodArguments, IMessageBus messageBus,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource,
        int iterations, int seed, SchedulerKind scheduler)
        : base(testCase, displayName, skipReason, constructorArguments, testMethodArguments, messageBus,
            aggregator, cancellationTokenSource)
    {
        _iterations = iterations;
        _seed = seed;
        _scheduler = scheduler;
    }

    protected override Task<RunSummary> RunTestAsync() =>
        new FrayXunitTestRunner(new XunitTest(TestCase, DisplayName), MessageBus, TestClass,
            ConstructorArguments, TestMethod, TestMethodArguments, SkipReason, BeforeAfterAttributes,
            new ExceptionAggregator(Aggregator), CancellationTokenSource,
            _iterations, _seed, _scheduler).RunAsync();
}

public class FrayXunitTestRunner : XunitTestRunner
{
    private readonly int _iterations;
    private readonly int _seed;
    private readonly SchedulerKind _scheduler;

    public FrayXunitTestRunner(ITest test, IMessageBus messageBus, Type testClass,
        object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments,
        string? skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource,
        int iterations, int seed, SchedulerKind scheduler)
        : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments,
            skipReason, beforeAfterAttributes, aggregator, cancellationTokenSource)
    {
        _iterations = iterations;
        _seed = seed;
        _scheduler = scheduler;
    }

    protected override Task<decimal> InvokeTestMethodAsync(ExceptionAggregator aggregator) =>
        new FrayTestInvoker(Test, MessageBus, TestClass, ConstructorArguments, TestMethod,
            TestMethodArguments, BeforeAfterAttributes, aggregator, CancellationTokenSource,
            _iterations, _seed, _scheduler).RunAsync();
}

public class FrayTestInvoker : XunitTestInvoker
{
    private readonly int _iterations;
    private readonly int _seed;
    private readonly SchedulerKind _scheduler;

    public FrayTestInvoker(ITest test, IMessageBus messageBus, Type testClass,
        object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments,
        IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        int iterations, int seed, SchedulerKind scheduler)
        : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments,
            beforeAfterAttributes, aggregator, cancellationTokenSource)
    {
        _iterations = iterations;
        _seed = seed;
        _scheduler = scheduler;
    }

    protected override object? CallTestMethod(object testClassInstance)
    {
        if (typeof(Task).IsAssignableFrom(TestMethod.ReturnType))
        {
            throw new NotSupportedException(
                "[FrayFact] does not support async test methods; use a synchronous test " +
                "body (it may start controlled async work and Wait for it).");
        }

        var configuration = new FrayConfiguration
        {
            Iterations = _iterations,
            Seed = _seed == FrayFactAttribute.NoSeed ? null : _seed,
            Scheduler = _scheduler,
        };
        var result = FrayTestRunner.Run(() =>
        {
            try
            {
                TestMethod.Invoke(testClassInstance, TestMethodArguments);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }, configuration);
        result.ThrowIfBugFound();
        return null;
    }
}
