using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace Wasmtime.Tests;

public sealed class LinuxArm64ReproFactAttribute : FactAttribute
{
    private const string ReproEnvVar = "WASMTIME_DOTNET_RUN_THREADED_REPRO";

    public LinuxArm64ReproFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.Arm64)
        {
            Skip = "Threaded repro is currently scoped to linux-arm64.";
            return;
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(ReproEnvVar), "1", StringComparison.Ordinal))
        {
            Skip = $"Set {ReproEnvVar}=1 to run the threaded execution repro test.";
        }
    }
}

public class ThreadedExecutionReproTests
{
    [LinuxArm64ReproFact]
    public void ItReproducesThreadedExecutionContentionDuringHostCallback()
    {
        using var config = new Config()
            .WithWasmThreads(true)
            .WithSharedMemory(true)
            .WithEpochInterruption(true);
        using var engine = new Engine(config);
        using var module = Module.FromText(
            engine,
            "ThreadedExecutionRepro",
            """
            (module
              (import "wasi" "thread-spawn" (func $thread_spawn (param i32) (result i32)))
              (import "env" "main_block" (func $main_block (param i32) (result i32)))
              (import "wasi_snapshot_preview1" "sched_yield" (func $sched_yield (result i32)))
              (memory (export "memory") 1 1 shared)
              (func (export "run")
                i32.const 0
                call $thread_spawn
                drop
                i32.const 250
                call $main_block
                drop)
              (func (export "wasi_thread_start") (param i32 i32)
                (loop $yield_loop
                  call $sched_yield
                  drop
                  br $yield_loop)))
            """);

        using var store = new Store(engine);
        using var linker = new Linker(engine);

        using var workerReady = new ManualResetEventSlim(false);
        using var workerFinished = new ManualResetEventSlim(false);

        Exception workerError = null;
        var workerCompletedDuringMainBlock = false;
        var workerCompletedMilliseconds = -1L;
        var workerStartedMilliseconds = -1L;

        var stopwatch = Stopwatch.StartNew();
        Instance instance = null;

        linker.Define("env", "main_block", Function.FromCallback(store, (int timeoutMs) =>
        {
            workerCompletedDuringMainBlock = workerFinished.Wait(TimeSpan.FromMilliseconds(timeoutMs));
            return workerCompletedDuringMainBlock ? 1 : 0;
        }));

        linker.Define("wasi_snapshot_preview1", "sched_yield", Function.FromCallback(store, () => 0));

        linker.Define("wasi", "thread-spawn", Function.FromCallback(store, (int startArg) =>
        {
            var worker = new Thread(() =>
            {
                try
                {
                    workerReady.Set();
                    Interlocked.Exchange(ref workerStartedMilliseconds, stopwatch.ElapsedMilliseconds);

                    var wasiThreadStart = instance.GetAction<int, int>("wasi_thread_start");
                    wasiThreadStart.Should().NotBeNull();
                    wasiThreadStart!(2, startArg);
                }
                catch (Exception ex)
                {
                    workerError = ex;
                }
                finally
                {
                    Interlocked.Exchange(ref workerCompletedMilliseconds, stopwatch.ElapsedMilliseconds);
                    workerFinished.Set();
                }
            })
            {
                IsBackground = true,
                Name = "wasmtime-threaded-repro-worker"
            };

            worker.Start();

            workerReady.Wait(TimeSpan.FromSeconds(1))
                .Should()
                .BeTrue("thread-spawn should create the worker before returning to wasm");

            return 1;
        }));

        instance = linker.Instantiate(store, module);
        var run = instance.GetFunction("run");
        run.Should().NotBeNull();

        store.SetEpochDeadline(1);
        const int interruptAfterMilliseconds = 450;

        using var cts = new CancellationTokenSource(interruptAfterMilliseconds);
        using var tokenRegistration = cts.Token.Register(engine.IncrementEpoch);

        try
        {
            run!.Invoke();
        }
        catch (TrapException ex) when (ex.Message.Contains("interrupt", StringComparison.OrdinalIgnoreCase))
        {
            // The interrupt can race with host callback timing and trap the main call.
        }

        if (!workerFinished.Wait(TimeSpan.FromSeconds(2)))
        {
            for (var i = 0; i < 4; i++)
            {
                engine.IncrementEpoch();
                Thread.Sleep(25);
            }
        }

        workerFinished.Wait(TimeSpan.FromSeconds(2))
            .Should().BeTrue("worker should finish once epoch interruption is raised");

        workerStartedMilliseconds.Should().BeGreaterOrEqualTo(0, "worker invocation should start");
        workerCompletedMilliseconds.Should().BeGreaterOrEqualTo(0, "worker invocation should complete");

        var workerCallStackExhausted =
            workerError is TrapException trap &&
            trap.Message.Contains("call stack exhausted", StringComparison.OrdinalIgnoreCase);

        var reproduced =
            !workerCompletedDuringMainBlock ||
            workerCallStackExhausted;

        Console.WriteLine(
            "Threaded repro outcome: " +
            $"reproduced={reproduced} " +
            $"workerCallStackExhausted={workerCallStackExhausted} " +
            $"workerStartedMs={Volatile.Read(ref workerStartedMilliseconds)} " +
            $"workerCompletedMs={Volatile.Read(ref workerCompletedMilliseconds)} " +
            $"completedDuringMainBlock={workerCompletedDuringMainBlock} " +
            $"workerError={workerError}");

        reproduced.Should().BeTrue(
            "expected worker call to block during host callback reentry or trap with call stack exhaustion, " +
            $"workerError={workerError}, " +
            $"workerStartedMs={Volatile.Read(ref workerStartedMilliseconds)}, " +
            $"workerCompletedMs={Volatile.Read(ref workerCompletedMilliseconds)}, " +
            $"completedDuringMainBlock={workerCompletedDuringMainBlock}");
    }
}
