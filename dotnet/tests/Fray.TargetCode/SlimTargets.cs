namespace Fray.TargetCode;

/// <summary>
/// Plain code using <see cref="SemaphoreSlim"/> and
/// <see cref="ManualResetEventSlim"/> (no Fray reference), plus an
/// <c>async void</c> usage that Fray must reject loudly.
/// </summary>
public static class SlimTargets
{
    private sealed class Counter
    {
        private int _value;

        public int Value => _value;

        public void Increment()
        {
            var tmp = _value;
            _value = tmp + 1;
        }

        public void Decrement()
        {
            var tmp = _value;
            _value = tmp - 1;
        }
    }

    public static void SemaphoreSlimMutex()
    {
        var semaphore = new SemaphoreSlim(1);
        var counter = new Counter();

        void Increment()
        {
            semaphore.Wait();
            try
            {
                counter.Increment();
            }
            finally
            {
                semaphore.Release();
            }
        }

        var t1 = new Thread(Increment);
        var t2 = new Thread(Increment);
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();
        if (counter.Value != 2)
        {
            throw new InvalidOperationException($"SemaphoreSlim mutex went wrong: counter is {counter.Value}.");
        }
    }

    public static void SemaphoreSlimTooWide()
    {
        var semaphore = new SemaphoreSlim(2);
        var inside = new Counter();

        void Enter()
        {
            semaphore.Wait();
            try
            {
                inside.Increment();
                if (inside.Value > 1)
                {
                    throw new InvalidOperationException("Two threads inside the critical section.");
                }
                inside.Decrement();
            }
            finally
            {
                semaphore.Release();
            }
        }

        var t1 = new Thread(Enter);
        var t2 = new Thread(Enter);
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();
    }

    public static void ResetEventPublication()
    {
        var ready = new ManualResetEventSlim(false);
        var value = new Counter();

        var producer = new Thread(() =>
        {
            value.Increment();
            ready.Set();
        });
        producer.Start();

        ready.Wait();
        if (value.Value != 1)
        {
            throw new InvalidOperationException("Observed the event before the write.");
        }
        producer.Join();
    }

    public static void AsyncVoidUsage()
    {
        async void FireAndForget()
        {
            await Task.Delay(1);
        }

        FireAndForget();
    }
}
