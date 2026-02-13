using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Wasmtime.Tests;

public class AsyncExecutionTests
{
    [Fact]
    public async Task PollingCanYieldAndResumeToCompletion()
    {
        if (!TryCreateAsyncEnvironment(out var engine, out var module, out var store, out var linker))
        {
            return;
        }

        using (engine)
        using (module)
        using (store)
        using (linker)
        {
            store.Fuel = 8_000_000;
            store.SetFuelAsyncYieldInterval(10_000);

            var instance = await linker.InstantiateAsync(store, module);
            var countdown = instance.GetFunction("countdown");

            using var future = countdown.BeginInvokeAsync(new[] { (ValueBox)250_000L });

            var yielded = false;
            for (var i = 0; i < 100_000 && !future.Poll(); i++)
            {
                yielded = true;
            }

            future.IsCompleted.Should().BeTrue("the countdown should eventually complete");
            yielded.Should().BeTrue("the async call should have yielded at least once");
            future.GetResult().Should().Be(0L);
        }
    }

    [Fact]
    public async Task InvokeAsyncCanBeCancelledBetweenYields()
    {
        if (!TryCreateAsyncEnvironment(out var engine, out var module, out var store, out var linker))
        {
            return;
        }

        using (engine)
        using (module)
        using (store)
        using (linker)
        {
            store.Fuel = ulong.MaxValue;
            store.SetFuelAsyncYieldInterval(10_000);

            var instance = await linker.InstantiateAsync(store, module);
            var spin = instance.GetFunction("spin");

            Func<Task> action = async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await spin.InvokeAsync(cancellationToken: cts.Token);
            };

            await action.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task InFlightFutureBlocksStoreReentry()
    {
        if (!TryCreateAsyncEnvironment(out var engine, out var module, out var store, out var linker))
        {
            return;
        }

        using (engine)
        using (module)
        using (store)
        using (linker)
        {
            store.Fuel = 8_000_000;
            store.SetFuelAsyncYieldInterval(10_000);

            var instance = await linker.InstantiateAsync(store, module);
            var spin = instance.GetFunction("spin");
            var countdown = instance.GetFunction("countdown");

            using var future = spin.BeginInvokeAsync();

            Action action = () => countdown.Invoke(1L);

            action.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*in-flight asynchronous call*");
        }
    }

    [Fact]
    public void SyncInstantiateThrowsForAsyncStore()
    {
        if (!TryCreateAsyncEnvironment(out var engine, out var module, out var store, out var linker))
        {
            return;
        }

        using (engine)
        using (module)
        using (store)
        using (linker)
        {
            Action action = () => linker.Instantiate(store, module);

            action.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Use Linker.InstantiateAsync*");
        }
    }

    private static bool TryCreateAsyncEnvironment(out Engine engine, out Module module, out Store store, out Linker linker)
    {
        engine = null;
        module = null;
        store = null;
        linker = null;

        try
        {
            var config = new Config()
                .WithMemory64(true)
                .WithAsyncSupport(true)
                .WithFuelConsumption(true);

            engine = new Engine(config);
            module = Module.FromTextFile(engine, Path.Combine("Modules", "AsyncYield.wat"));
            store = new Store(engine);
            linker = new Linker(engine);
            return true;
        }
        catch (NotSupportedException)
        {
            linker?.Dispose();
            store?.Dispose();
            module?.Dispose();
            engine?.Dispose();
            return false;
        }
    }
}
