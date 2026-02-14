#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wasmtime;
using Xunit;

namespace Wasmtime.Tests
{
    public class ThreadedStoreReentryTests
    {
        [Fact]
        public void ItThrowsManagedErrorForConcurrentStoreAccessFromWorkerThread()
        {
            using var engine = new Engine();
            using var module = Module.FromText(
                engine,
                "threaded-store-reentry",
                """
                (module
                  (type $thread_fn (func (param i32) (result i32)))
                  (import "env" "spawn" (func $spawn (param i32 i32) (result i32)))
                  (import "env" "wait_for_signal" (func $wait_for_signal))
                  (import "env" "signal" (func $signal))
                  (memory (export "memory") 1)
                  (table (export "__indirect_function_table") 3 funcref)
                  (elem (i32.const 1) $thread_entry $target)

                  (func $target (param $arg i32) (result i32)
                    call $signal
                    i32.const 0)

                  (func $thread_entry (param $arg i32) (result i32)
                    local.get $arg
                    i32.load
                    local.get $arg
                    i32.const 4
                    i32.add
                    i32.load
                    call_indirect (type $thread_fn)
                    drop
                    i32.const 0)

                  (func (export "run") (result i32)
                    i32.const 1024
                    i32.const 2
                    i32.store
                    i32.const 1028
                    i32.const 0
                    i32.store
                    i32.const 1
                    i32.const 1024
                    call $spawn
                    drop
                    call $wait_for_signal
                    i32.const 0))
                """);

            using var store = new Store(engine);
            using var linker = new Linker(engine);

            var signaled = new ManualResetEventSlim(false);
            var workerCompleted = new ManualResetEventSlim(false);
            Exception? workerException = null;
            Table? table = null;

            linker.DefineFunction("env", "spawn", (int startRoutine, int startArg) =>
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        var start = table!.GetElement(unchecked((uint)startRoutine)) as Function;
                        start.Should().NotBeNull("table entry {0} should contain a start routine", startRoutine);
                        var startFunc = start!.WrapFunc<int, int>();
                        startFunc.Should().NotBeNull();
                        startFunc!(startArg).Should().Be(0);
                    }
                    catch (Exception ex)
                    {
                        workerException = ex;
                    }
                    finally
                    {
                        workerCompleted.Set();
                    }
                });

                thread.IsBackground = true;
                thread.Name = "wasmtime-threaded-store-reentry";
                thread.Start();
                return 0;
            });

            linker.DefineFunction("env", "wait_for_signal", () =>
            {
                WaitHandle.WaitAny(
                    [signaled.WaitHandle, workerCompleted.WaitHandle],
                    TimeSpan.FromSeconds(3))
                    .Should()
                    .NotBe(WaitHandle.WaitTimeout, "worker thread should either signal or report a managed failure");
            });

            linker.DefineFunction("env", "signal", () =>
            {
                signaled.Set();
            });

            var instance = linker.Instantiate(store, module);
            table = instance.GetTable("__indirect_function_table");
            table.Should().NotBeNull();

            var run = instance.GetFunction("run")!.WrapFunc<int>();
            run.Should().NotBeNull();
            run!().Should().Be(0);

            workerCompleted.Wait(TimeSpan.FromSeconds(3))
                .Should()
                .BeTrue("worker thread should complete");

            workerException
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message
                .Should()
                .Contain("not supported");
        }

        [Fact]
        public void ItSupportsThreadedExecutionWithOneStorePerThread()
        {
            using var engine = new Engine();
            using var module = Module.FromText(
                engine,
                "threaded-store-per-thread",
                """
                (module
                  (func (export "bump") (param $value i32) (result i32)
                    local.get $value
                    i32.const 1
                    i32.add))
                """);

            using var messageQueue = new BlockingCollection<int>(boundedCapacity: 1);
            using var producerCompleted = new ManualResetEventSlim(false);
            using var consumerCompleted = new ManualResetEventSlim(false);

            Exception? producerException = null;
            Exception? consumerException = null;
            var observedValue = int.MinValue;

            var producerThread = new Thread(() =>
            {
                try
                {
                    const int payload = 41;
                    using var producerStore = new Store(engine);
                    using var producerLinker = new Linker(engine);
                    var producerInstance = producerLinker.Instantiate(producerStore, module);
                    var bump = producerInstance.GetFunction<int, int>("bump");
                    bump.Should().NotBeNull();

                    messageQueue.Add(bump!(payload));
                }
                catch (Exception ex)
                {
                    producerException = ex;
                }
                finally
                {
                    messageQueue.CompleteAdding();
                    producerCompleted.Set();
                }
            })
            {
                IsBackground = true,
                Name = "wasmtime-producer-store-thread"
            };

            var consumerThread = new Thread(() =>
            {
                try
                {
                    using var consumerStore = new Store(engine);
                    using var consumerLinker = new Linker(engine);
                    var consumerInstance = consumerLinker.Instantiate(consumerStore, module);
                    var bump = consumerInstance.GetFunction<int, int>("bump");
                    bump.Should().NotBeNull();

                    var expected = messageQueue.Take();
                    observedValue = bump!(expected);
                }
                catch (Exception ex)
                {
                    consumerException = ex;
                }
                finally
                {
                    consumerCompleted.Set();
                }
            })
            {
                IsBackground = true,
                Name = "wasmtime-consumer-store-thread"
            };

            consumerThread.Start();
            producerThread.Start();

            producerCompleted.Wait(TimeSpan.FromSeconds(3))
                .Should()
                .BeTrue("producer thread should finish");

            consumerCompleted.Wait(TimeSpan.FromSeconds(3))
                .Should()
                .BeTrue("consumer thread should finish");

            producerException.Should().BeNull();
            consumerException.Should().BeNull();
            observedValue.Should().Be(43);
        }

        [Fact]
        public void ItDemonstratesBusyRetryWithoutProgressWhenStoreIsContendedAcrossThreads()
        {
            using var engine = new Engine();
            using var module = Module.FromText(
                engine,
                "threaded-store-livelock-retry",
                """
                (module
                  (type $thread_fn (func (param i32) (result i32)))
                  (import "env" "spawn" (func $spawn (param i32 i32) (result i32)))
                  (import "env" "wait_window" (func $wait_window))
                  (memory (export "memory") 1)
                  (table (export "__indirect_function_table") 2 funcref)
                  (elem (i32.const 1) $target)

                  (func $target (param $arg i32) (result i32)
                    i32.const 0)

                  (func (export "run")
                    i32.const 1
                    i32.const 0
                    call $spawn
                    drop
                    call $wait_window))
                """);

            using var store = new Store(engine);
            using var linker = new Linker(engine);

            using var workerStarted = new ManualResetEventSlim(false);
            using var workerCompleted = new ManualResetEventSlim(false);

            var stopWorker = 0;
            var contentionFailures = 0;
            var successfulInvocations = 0;
            Exception? workerException = null;
            Table? table = null;

            linker.DefineFunction("env", "spawn", (int startRoutine, int startArg) =>
            {
                var thread = new Thread(() =>
                {
                    workerStarted.Set();
                    while (Interlocked.CompareExchange(ref stopWorker, 0, 0) == 0)
                    {
                        try
                        {
                            var start = table!.GetElement(unchecked((uint)startRoutine)) as Function;
                            start.Should().NotBeNull();
                            var startFunc = start!.WrapFunc<int, int>();
                            startFunc.Should().NotBeNull();
                            _ = startFunc!(startArg);
                            Interlocked.Increment(ref successfulInvocations);
                        }
                        catch (InvalidOperationException ex)
                            when (ex.Message.Contains("Concurrent access to a Store"))
                        {
                            Interlocked.Increment(ref contentionFailures);
                        }
                        catch (Exception ex)
                        {
                            workerException = ex;
                            break;
                        }
                    }

                    workerCompleted.Set();
                });

                thread.IsBackground = true;
                thread.Name = "wasmtime-threaded-store-busy-retry";
                thread.Start();
                return 0;
            });

            linker.DefineFunction("env", "wait_window", () =>
            {
                workerStarted.Wait(TimeSpan.FromSeconds(3))
                    .Should()
                    .BeTrue("worker should start while run() is active");

                Thread.Sleep(TimeSpan.FromMilliseconds(200));
                Interlocked.Exchange(ref stopWorker, 1);
            });

            var instance = linker.Instantiate(store, module);
            table = instance.GetTable("__indirect_function_table");
            table.Should().NotBeNull();

            var run = instance.GetAction("run");
            run.Should().NotBeNull();
            run!();

            workerCompleted.Wait(TimeSpan.FromSeconds(3))
                .Should()
                .BeTrue("worker should stop when the retry window closes");

            workerException.Should().BeNull();
            successfulInvocations.Should().Be(0, "contended shared-store retries should make no progress");
            contentionFailures.Should().BeGreaterThan(100, "busy retries should accumulate quickly and indicate CPU churn");
        }

        [Fact]
        public async Task ItAllowsQueuedThreadPumpAccessInsideAsyncCallbackExecution()
        {
            Engine? engine = null;
            Module? module = null;
            Store? store = null;
            Linker? linker = null;

            try
            {
                using var config = new Config().WithAsyncSupport(true);
                engine = new Engine(config);
                module = Module.FromText(
                    engine,
                    "threaded-async-pump-reentry",
                    """
                    (module
                      (import "env" "spawn" (func $spawn (param i32) (result i32)))
                      (import "env" "wait" (func $wait))
                      (global (export "state") i32 (i32.const 17))
                      (func (export "worker") (result i32)
                        i32.const 7)
                      (func (export "run")
                        i32.const 0
                        call $spawn
                        drop
                        call $wait))
                    """);
                store = new Store(engine);
                linker = new Linker(engine);
            }
            catch (NotSupportedException)
            {
                linker?.Dispose();
                store?.Dispose();
                module?.Dispose();
                engine?.Dispose();
                return;
            }

            using (engine)
            using (module)
            using (store)
            using (linker)
            {
                var queuedWork = new ConcurrentQueue<Action>();
                var pumpAttempted = new ManualResetEventSlim(false);
                var pumpCompleted = new ManualResetEventSlim(false);
                Exception? pumpException = null;
                Instance? instance = null;
                var workerResolved = 0;
                var globalRead = 0;

                linker.DefineFunction("env", "spawn", (int _) =>
                {
                    queuedWork.Enqueue(() =>
                    {
                        // Mirrors the integration scheduler pattern where queued worker
                        // work needs to touch instance exports while the async call-future
                        // for run() is still in flight.
                        var worker = instance!.GetFunction("worker");
                        worker.Should().NotBeNull();
                        Interlocked.Increment(ref workerResolved);

                        var global = instance.GetGlobal("state");
                        global.Should().NotBeNull();
                        var accessor = global!.Wrap<int>();
                        accessor.Should().NotBeNull();
                        globalRead = accessor!.GetValue();
                    });

                    return 0;
                });

                linker.DefineFunction("env", "wait", () =>
                {
                    if (queuedWork.TryDequeue(out var work))
                    {
                        pumpAttempted.Set();
                        try
                        {
                            work();
                        }
                        catch (Exception ex)
                        {
                            pumpException = ex;
                        }
                        finally
                        {
                            pumpCompleted.Set();
                        }
                    }
                });

                instance = await linker.InstantiateAsync(store, module);
                var run = instance.GetFunction("run");
                run.Should().NotBeNull();

                await run!.InvokeAsync();

                pumpAttempted.IsSet.Should().BeTrue("the queued worker should be pumped during wait()");
                pumpCompleted.Wait(TimeSpan.FromSeconds(1))
                    .Should()
                    .BeTrue("queued work should complete during callback execution");
                pumpException.Should().BeNull("store access should be allowed while re-entering from async callback on the execution owner thread");
                workerResolved.Should().Be(1);
                globalRead.Should().Be(17);
            }
        }
    }
}
