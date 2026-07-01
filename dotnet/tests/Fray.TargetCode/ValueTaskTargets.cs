namespace Fray.TargetCode;

/// <summary>
/// Plain <c>async ValueTask</c> code (no Fray reference): value-task builders,
/// awaiting ValueTasks (both suspended and synchronously completed), and
/// <c>ValueTask&lt;T&gt;.Result</c>.
/// </summary>
public static class ValueTaskTargets
{
    private sealed class Counter
    {
        private int _value;

        public int Value => _value;

        public void Force(int value) => _value = value;
    }

    private static async ValueTask IncrementAcrossAwaitAsync(Counter counter)
    {
        var tmp = counter.Value;
        await Task.Delay(1);
        counter.Force(tmp + 1);
    }

    public static void ValueTaskLostUpdate()
    {
        var counter = new Counter();
        var v1 = IncrementAcrossAwaitAsync(counter);
        var v2 = IncrementAcrossAwaitAsync(counter);
        v1.AsTask().Wait();
        v2.AsTask().Wait();
        if (counter.Value != 2)
        {
            throw new InvalidOperationException($"Lost update: counter is {counter.Value}, expected 2.");
        }
    }

    private static async ValueTask<int> AddAsync()
    {
        var a = Task.Run(() => 20);
        var b = Task.Run(() => 22);
        return await a + await b;
    }

    public static void ValueTaskComposition()
    {
        var sum = AddAsync().Result;
        if (sum != 42)
        {
            throw new InvalidOperationException($"ValueTask composition went wrong: {sum}.");
        }
    }

    private static async ValueTask<int> CompletedAsync()
    {
        await ValueTask.CompletedTask;
        return 5;
    }

    private static async ValueTask<int> ChainAsync()
    {
        // Awaits a suspended ValueTask<int> and a synchronously completed one.
        return await AddAsync() + await CompletedAsync();
    }

    public static void ValueTaskChain()
    {
        var sum = ChainAsync().Result;
        if (sum != 47)
        {
            throw new InvalidOperationException($"ValueTask chain went wrong: {sum}.");
        }
    }
}
