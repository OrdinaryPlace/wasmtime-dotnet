#nullable enable
using System;
using System.Threading;
using FluentAssertions;
using Wasmtime;
using Xunit;

namespace Wasmtime.Tests
{
    public class ThreadedStoreReentryTests
    {
        [Fact]
        public void ItCanRunIndirectStartRoutineFromWorkerThreadWhileMainThreadWaitsInHostCallback()
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
                signaled.Wait(TimeSpan.FromSeconds(3))
                    .Should()
                    .BeTrue("worker thread should re-enter wasm and signal completion");
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
                .BeTrue("worker thread should complete after signaling");
            workerException.Should().BeNull();
        }
    }
}
