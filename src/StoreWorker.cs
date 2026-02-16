using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Wasmtime
{
    /// <summary>
    /// Runs Wasmtime work on a dedicated thread using a dedicated <see cref="Store"/>.
    /// </summary>
    /// <typeparam name="TState">
    /// The worker state initialized on the worker thread (for example wrapped functions or an instance).
    /// </typeparam>
    /// <remarks>
    /// Use this to coordinate one-Store-per-thread execution with message passing.
    /// </remarks>
    public sealed class StoreWorker<TState> : IDisposable
        where TState : notnull
    {
        /// <summary>
        /// Creates a new worker thread with its own <see cref="Store"/> and <see cref="Linker"/>.
        /// </summary>
        /// <param name="engine">The shared engine to use for this worker store.</param>
        /// <param name="initialize">
        /// Called on the worker thread to configure linker imports and initialize worker state.
        /// </param>
        /// <param name="threadName">Optional worker thread name.</param>
        public StoreWorker(Engine engine, Func<Store, Linker, TState> initialize, string? threadName = null)
        {
            if (engine is null)
            {
                throw new ArgumentNullException(nameof(engine));
            }

            if (initialize is null)
            {
                throw new ArgumentNullException(nameof(initialize));
            }

            thread = new Thread(() => WorkerLoop(engine, initialize))
            {
                IsBackground = true,
                Name = string.IsNullOrWhiteSpace(threadName) ? "wasmtime-store-worker" : threadName
            };

            thread.Start();
            startupCompleted.Wait();

            var fault = Volatile.Read(ref workerFault);
            if (fault is not null)
            {
                workItems.CompleteAdding();
                thread.Join();
                startupCompleted.Dispose();
                workItems.Dispose();
                throw CreateWorkerTerminatedException(fault);
            }
        }

        /// <summary>
        /// Dispatches work to the worker thread and waits for completion.
        /// </summary>
        /// <param name="work">The callback to execute on the worker thread.</param>
        /// <returns>The callback result.</returns>
        public TResult Invoke<TResult>(Func<Store, TState, TResult> work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (Environment.CurrentManagedThreadId == workerThreadId)
            {
                var localStore = workerStore;
                var localState = workerState;

                if (localStore is null || localState is null)
                {
                    throw new InvalidOperationException("Worker state is not available.");
                }

                return work(localStore, localState);
            }

            ThrowIfUnavailable();

            var workItem = new WorkItem<TResult>(work);
            try
            {
                workItems.Add(workItem);
            }
            catch (InvalidOperationException)
            {
                ThrowIfUnavailable();
                throw;
            }

            return workItem.Wait();
        }

        /// <summary>
        /// Dispatches work to the worker thread and waits for completion.
        /// </summary>
        /// <param name="work">The callback to execute on the worker thread.</param>
        public void Invoke(Action<Store, TState> work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            _ = Invoke((store, state) =>
            {
                work(store, state);
                return true;
            });
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            workItems.CompleteAdding();

            if (Environment.CurrentManagedThreadId != workerThreadId)
            {
                thread.Join();
            }

            startupCompleted.Dispose();
            workItems.Dispose();
        }

        private void WorkerLoop(Engine engine, Func<Store, Linker, TState> initialize)
        {
            workerThreadId = Environment.CurrentManagedThreadId;

            try
            {
                using var store = new Store(engine);
                using var linker = new Linker(engine);

                var state = initialize(store, linker);
                workerStore = store;
                workerState = state;

                startupCompleted.Set();

                foreach (var item in workItems.GetConsumingEnumerable())
                {
                    item.Execute(store, state);
                }
            }
            catch (Exception ex)
            {
                workerFault = ex;
                startupCompleted.Set();
            }
            finally
            {
                workItems.CompleteAdding();

                if (workerFault is not null)
                {
                    var workerTerminatedException = CreateWorkerTerminatedException(workerFault);
                    while (workItems.TryTake(out var pending))
                    {
                        pending.Fail(workerTerminatedException);
                    }
                }

                workerStore = null;
                workerState = default;
            }
        }

        private void ThrowIfUnavailable()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(typeof(StoreWorker<TState>).FullName);
            }

            var fault = Volatile.Read(ref workerFault);
            if (fault is not null)
            {
                throw CreateWorkerTerminatedException(fault);
            }
        }

        private static InvalidOperationException CreateWorkerTerminatedException(Exception inner)
        {
            return new InvalidOperationException("The store worker thread terminated unexpectedly.", inner);
        }

        private interface IWorkItem
        {
            void Execute(Store store, TState state);
            void Fail(Exception exception);
        }

        private sealed class WorkItem<TResult> : IWorkItem
        {
            internal WorkItem(Func<Store, TState, TResult> callback)
            {
                this.callback = callback;
            }

            public void Execute(Store store, TState state)
            {
                try
                {
                    completion.SetResult(callback(store, state));
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }

            public void Fail(Exception exception)
            {
                completion.TrySetException(exception);
            }

            internal TResult Wait()
            {
                return completion.Task.GetAwaiter().GetResult();
            }

            private readonly Func<Store, TState, TResult> callback;
            private readonly TaskCompletionSource<TResult> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly BlockingCollection<IWorkItem> workItems = new();
        private readonly ManualResetEventSlim startupCompleted = new(false);
        private readonly Thread thread;
        private int disposed;
        private int workerThreadId;
        private Exception? workerFault;
        private Store? workerStore;
        private TState? workerState;
    }
}
