using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Wasmtime
{
    /// <summary>
    /// Represents context about a <see cref="Wasmtime.Store"/>.
    /// </summary>
    internal readonly ref struct StoreContext
    {
        internal StoreContext(IntPtr handle)
        {
            this.handle = handle;
        }

        internal void GC()
        {
            Native.wasmtime_context_gc(handle);
        }

        internal ulong GetFuel()
        {
            var error = Native.wasmtime_context_get_fuel(handle, out ulong fuel);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }

            return fuel;
        }

        internal void SetFuel(ulong fuel)
        {
            var error = Native.wasmtime_context_set_fuel(handle, fuel);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        internal Store Store
        {
            get
            {
                var data = Native.wasmtime_context_get_data(handle);

                // Since this is a weak handle, it could be `null` if the target object (`Store`)
                // was already collected. However, this would be an error in wasmtime-dotnet
                // itself because the `Store` must be kept alive when this is called, and
                // therefore this should never happen (otherwise, when the `Store` was already
                // GCed, its `Handle` might also be GCed and have run its finalizer, which
                // would already have freed the `GCHandle` (from the Finalize callback) and thus
                // it would already be undefined behavior to try to get the `GCHandle` from the
                // `IntPtr` value).
                var targetStore = (Store?)GCHandle.FromIntPtr(data).Target!;

                return targetStore;
            }
        }

        internal void SetWasiConfiguration(WasiConfiguration config)
        {
            var wasi = config.Build();
            var error = Native.wasmtime_context_set_wasi(handle, wasi.DangerousGetHandle());
            wasi.SetHandleAsInvalid();

            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        /// <summary>
        /// Configures the relative deadline at which point WebAssembly code will trap.
        /// </summary>
        /// <param name="deadline"></param>
        public void SetEpochDeadline(ulong deadline)
        {
            Native.wasmtime_context_set_epoch_deadline(handle, deadline);
        }

        /// <summary>
        /// Configures how often async WebAssembly execution should yield based on fuel usage.
        /// </summary>
        /// <param name="interval">The amount of fuel to consume between yields. Use 0 to disable yielding.</param>
        public void SetFuelAsyncYieldInterval(ulong interval)
        {
            var error = Native.wasmtime_context_fuel_async_yield_interval(handle, interval);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        /// <summary>
        /// Configures epoch-deadline expiration to yield and then update the deadline.
        /// </summary>
        /// <param name="deadlineDelta">The deadline delta to apply after each yield.</param>
        public void SetEpochDeadlineAsyncYieldAndUpdate(ulong deadlineDelta)
        {
            var error = Native.wasmtime_context_epoch_deadline_async_yield_and_update(handle, deadlineDelta);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }

        private static class Native
        {
            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_context_gc(IntPtr handle);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_set_fuel(IntPtr handle, ulong fuel);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_get_fuel(IntPtr handle, out ulong fuel);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_set_wasi(IntPtr handle, IntPtr config);

            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_context_set_epoch_deadline(IntPtr handle, ulong ticksBeyondCurrent);

            [DllImport(Engine.LibraryName, EntryPoint = "wasmtime_context_fuel_async_yield_interval")]
            private static extern IntPtr wasmtime_context_fuel_async_yield_interval_native(IntPtr handle, ulong interval);

            public static IntPtr wasmtime_context_fuel_async_yield_interval(IntPtr handle, ulong interval)
            {
                try
                {
                    return wasmtime_context_fuel_async_yield_interval_native(handle, interval);
                }
                catch (EntryPointNotFoundException ex)
                {
                    throw new NotSupportedException("Async support is not available in the loaded Wasmtime runtime.", ex);
                }
            }

            [DllImport(Engine.LibraryName, EntryPoint = "wasmtime_context_epoch_deadline_async_yield_and_update")]
            private static extern IntPtr wasmtime_context_epoch_deadline_async_yield_and_update_native(IntPtr handle, ulong delta);

            public static IntPtr wasmtime_context_epoch_deadline_async_yield_and_update(IntPtr handle, ulong delta)
            {
                try
                {
                    return wasmtime_context_epoch_deadline_async_yield_and_update_native(handle, delta);
                }
                catch (EntryPointNotFoundException ex)
                {
                    throw new NotSupportedException("Async support is not available in the loaded Wasmtime runtime.", ex);
                }
            }

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_context_get_data(IntPtr handle);
        }

        internal readonly IntPtr handle;
    }

    /// <summary>
    /// Represents a Wasmtime store.
    /// </summary>
    /// <remarks>
    /// A Wasmtime store may be sent between threads but cannot be used from more than one thread
    /// simultaneously.
    /// </remarks>
    public class Store : IDisposable
    {
        /// <summary>
        /// Constructs a new store.
        /// </summary>
        /// <param name="engine">The engine to use for the store.</param>
        public Store(Engine engine) : this(engine, null) { }

        /// <summary>
        /// Constructs a new store with the given context data.
        /// </summary>
        /// <param name="engine">The engine to use for the store.</param>
        /// <param name="data">The data to initialize the store with; this can later be accessed with the GetData function.</param>
        public Store(Engine engine, object? data)
        {
            if (engine is null)
            {
                throw new ArgumentNullException(nameof(engine));
            }

            isAsyncSupportEnabled = engine.IsAsyncSupportEnabled;
            this.data = data;

            // Allocate a weak GCHandle, so that it does not participate in keeping the Store alive.
            // Otherwise, the circular reference would prevent the Store from being finalized even
            // if it's no longer referenced by user code.
            // The weak handle will be used to get the originating Store object from a Caller's
            // context in host callbacks.
            var storeHandle = GCHandle.Alloc(this, GCHandleType.Weak);

            handle = new Handle(Native.wasmtime_store_new(engine.NativeHandle, (IntPtr)storeHandle, Finalizer));

        }

        /// <summary>
        /// Gets or sets the fuel available for WebAssembly code to consume while executing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For this property to work fuel consumption must be enabled via <see cref="Config.WithFuelConsumption(bool)"/>.
        /// </para>
        /// <para>
        /// WebAssembly execution will automatically consume fuel but if so desired the embedder can also consume fuel manually
        /// to account for relative costs of host functions, for example.
        /// </para>
        /// </remarks>
        /// <value>The fuel available for WebAssembly code to consume while executing.</value>
        public ulong Fuel
        {
            get
            {
                ulong fuel = Context.GetFuel();
                System.GC.KeepAlive(this);
                return fuel;
            }

            set
            {
                Context.SetFuel(value);
                System.GC.KeepAlive(this);
            }
        }

        /// <summary>
        /// Limit the resources that this store may consume. Note that the limits are only used to limit the creation/growth of resources in the future,
        /// this does not retroactively attempt to apply limits to the store.
        /// </summary>
        /// <param name="memorySize">the maximum number of bytes a linear memory can grow to. Growing a linear memory beyond this limit will fail.
        /// Pass in a null value to use the default value (unlimited)</param>
        /// <param name="tableElements">the maximum number of elements in a table. Growing a table beyond this limit will fail.
        /// Pass in a null value to use the default value (unlimited)</param>
        /// <param name="instances">the maximum number of instances that can be created for a Store. Module instantiation will fail if this limit is exceeded.
        /// Pass in a null value to use the default value (10000)</param>
        /// <param name="tables">the maximum number of tables that can be created for a Store. Module instantiation will fail if this limit is exceeded.
        /// Pass in a null value to use the default value (10000)</param>
        /// <param name="memories">the maximum number of linear memories that can be created for a Store. Instantiation will fail with an error if this limit is exceeded.
        /// Pass in a null value to use the default value (10000)</param>
        public void SetLimits(long? memorySize = null, uint? tableElements = null, long? instances = null, long? tables = null, long? memories = null)
        {
            if (memorySize.HasValue && memorySize.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(memorySize));
            }

            if (instances.HasValue && instances.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(instances));
            }

            if (tables.HasValue && tables.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tables));
            }

            if (memories.HasValue && memories.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(memories));
            }

            long tableElements64 = -1;
            if (tableElements.HasValue)
            {
                tableElements64 = tableElements.Value;
            }

            Native.wasmtime_store_limiter(NativeHandle, memorySize ?? -1, tableElements64, instances ?? -1, tables ?? -1, memories ?? -1);
        }

        /// <summary>
        /// Perform garbage collection within the given store.
        /// </summary>
        public void GC()
        {
            Context.GC();
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures WASI within the store.
        /// </summary>
        /// <param name="config">The WASI configuration to use.</param>
        public void SetWasiConfiguration(WasiConfiguration config)
        {
            Context.SetWasiConfiguration(config);
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures the relative deadline at which point WebAssembly code will trap.
        /// </summary>
        /// <param name="ticksBeyondCurrent"></param>
        public void SetEpochDeadline(ulong ticksBeyondCurrent)
        {
            Context.SetEpochDeadline(ticksBeyondCurrent);
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures how often async WebAssembly execution should yield based on fuel usage.
        /// </summary>
        /// <param name="interval">The amount of fuel to consume between yields. Use 0 to disable yielding.</param>
        public void SetFuelAsyncYieldInterval(ulong interval)
        {
            Context.SetFuelAsyncYieldInterval(interval);
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Configures epoch-deadline expiration to yield and then update the deadline.
        /// </summary>
        /// <param name="deadlineDelta">The deadline delta to apply after each yield.</param>
        public void SetEpochDeadlineAsyncYieldAndUpdate(ulong deadlineDelta)
        {
            Context.SetEpochDeadlineAsyncYieldAndUpdate(deadlineDelta);
            System.GC.KeepAlive(this);
        }

        /// <summary>
        /// Retrieves the data stored in the Store context
        /// </summary>
        public object? GetData() => data;

        /// <summary>
        /// Replaces the data stored in the Store context 
        /// </summary>
        public void SetData(object? data) => this.data = data;

        /// <inheritdoc/>
        public void Dispose()
        {
            handle.Dispose();
        }

        internal Handle NativeHandle
        {
            get
            {
                if (handle.IsInvalid || handle.IsClosed)
                {
                    throw new ObjectDisposedException(typeof(Store).FullName);
                }

                return handle;
            }
        }

        /// <summary>
        /// Gets the context of the store.
        /// </summary>
        /// <remarks>
        /// Note: Generally, you must keep the <see cref="Store"/> alive (by using
        /// <see cref="GC.KeepAlive(object)"/>) until the <see cref="StoreContext"/> is no longer
        /// used, to prevent the the <see cref="Handle"/> finalizer from prematurely deleting the
        /// store handle in the GC finalizer thread while the <see cref="StoreContext"/> is still
        /// in use.
        /// </remarks>
        internal StoreContext Context
        {
            get
            {
                if (handle.IsClosed)
                {
                    throw new ObjectDisposedException(typeof(Store).FullName);
                }

                EnsureAccessibleFromCurrentThread();
                return new StoreContext(Native.wasmtime_store_context(NativeHandle));
            }
        }

        internal bool IsAsyncSupportEnabled => isAsyncSupportEnabled;

        /// <summary>
        /// Gets the context of the store while an async call-future is in flight.
        /// </summary>
        internal StoreContext ContextForAsyncExecution
        {
            get
            {
                if (handle.IsClosed)
                {
                    throw new ObjectDisposedException(typeof(Store).FullName);
                }

                EnsureAccessibleFromCurrentThread(allowDuringAsyncExecution: true);
                return new StoreContext(Native.wasmtime_store_context(NativeHandle));
            }
        }

        /// <summary>
        /// Enters an execution scope for this store.
        /// </summary>
        /// <remarks>
        /// Wasmtime stores can move across threads but cannot be used concurrently from more than
        /// one thread. This scope tracks active wasm execution to prevent cross-thread re-entry
        /// from reaching undefined behavior in the unmanaged API.
        /// </remarks>
        internal ExecutionScope EnterExecutionScope(bool allowDuringAsyncExecution = false)
        {
            var currentThreadId = Environment.CurrentManagedThreadId;

            lock (executionSync)
            {
                if (asyncExecutionInProgress && !allowDuringAsyncExecution)
                {
                    var isReentrantOnExecutionOwner =
                        executionOwnerThreadId == currentThreadId &&
                        executionDepth > 0;
                    if (!isReentrantOnExecutionOwner)
                    {
                        throw CreateAsyncStoreAccessException();
                    }
                }

                if (executionOwnerThreadId == 0 || executionOwnerThreadId == currentThreadId)
                {
                    executionOwnerThreadId = currentThreadId;
                    executionDepth++;
                    return new ExecutionScope(this);
                }
            }

            throw CreateConcurrentStoreAccessException();
        }

        internal void EnsureAccessibleFromCurrentThread(bool allowDuringAsyncExecution = false)
        {
            var currentThreadId = Environment.CurrentManagedThreadId;

            lock (executionSync)
            {
                if (asyncExecutionInProgress && !allowDuringAsyncExecution)
                {
                    var isReentrantOnExecutionOwner =
                        executionOwnerThreadId == currentThreadId &&
                        executionDepth > 0;
                    if (!isReentrantOnExecutionOwner)
                    {
                        throw CreateAsyncStoreAccessException();
                    }
                }

                if (executionOwnerThreadId != 0 && executionOwnerThreadId != currentThreadId)
                {
                    throw CreateConcurrentStoreAccessException();
                }
            }
        }

        internal AsyncExecutionLease EnterAsyncExecutionLease()
        {
            lock (executionSync)
            {
                if (asyncExecutionInProgress)
                {
                    throw CreateAsyncStoreAccessException();
                }

                if (executionOwnerThreadId != 0)
                {
                    throw CreateConcurrentStoreAccessException();
                }

                asyncExecutionInProgress = true;
                return new AsyncExecutionLease(this);
            }
        }

        private void ExitExecutionScope()
        {
            var currentThreadId = Environment.CurrentManagedThreadId;

            lock (executionSync)
            {
                if (executionOwnerThreadId != currentThreadId || executionDepth <= 0)
                {
                    throw new InvalidOperationException("Invalid store execution scope state.");
                }

                executionDepth--;
                if (executionDepth == 0)
                {
                    executionOwnerThreadId = 0;
                }
            }
        }

        private void ExitAsyncExecutionLease()
        {
            lock (executionSync)
            {
                if (!asyncExecutionInProgress)
                {
                    throw new InvalidOperationException("Invalid async store execution lease state.");
                }

                asyncExecutionInProgress = false;
            }
        }

        internal const string ConcurrentStoreAccessMessage =
            "Concurrent access to a Store from multiple threads is not supported.";

        private static InvalidOperationException CreateConcurrentStoreAccessException()
        {
            return new InvalidOperationException(
                ConcurrentStoreAccessMessage + " " +
                "Use one Store per thread or serialize access so only one thread uses a Store at a time.");
        }

        private static InvalidOperationException CreateAsyncStoreAccessException()
        {
            return new InvalidOperationException(
                "A Store has an in-flight asynchronous call. Poll or dispose the call future before using this Store for other operations.");
        }

        internal readonly ref struct ExecutionScope
        {
            internal ExecutionScope(Store store)
            {
                this.store = store;
            }

            public void Dispose()
            {
                store.ExitExecutionScope();
            }

            private readonly Store store;
        }

        internal sealed class AsyncExecutionLease : IDisposable
        {
            internal AsyncExecutionLease(Store store)
            {
                this.store = store;
            }

            public void Dispose()
            {
                var store = Interlocked.Exchange(ref this.store, null);
                store?.ExitAsyncExecutionLease();
            }

            private Store? store;
        }

        internal class Handle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public Handle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                Native.wasmtime_store_delete(handle);
                return true;
            }
        }

        private static class Native
        {
            public delegate void Finalizer(IntPtr data);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_store_new(Engine.Handle engine, IntPtr data, Finalizer? finalizer);

            [DllImport(Engine.LibraryName)]
            public static extern IntPtr wasmtime_store_context(Handle store);

            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_store_delete(IntPtr store);

            [DllImport(Engine.LibraryName)]
            public static extern void wasmtime_store_limiter(Handle store, long memory_size, long table_elements, long instances, long tables, long memories);
        }

        private readonly Handle handle;
        private readonly object executionSync = new();
        private int executionOwnerThreadId;
        private int executionDepth;
        private bool asyncExecutionInProgress;
        private readonly bool isAsyncSupportEnabled;

        private object? data;

        private static readonly Native.Finalizer Finalizer = (p) => GCHandle.FromIntPtr(p).Free();
        
        // The caches below use the external struct type as key type. These structs contain
        // __private fields, but these are not interpreted and merely used for value comparison.
        private readonly ConcurrentDictionary<ExternFunc, Function> _externFunctionCache = new();
        private readonly ConcurrentDictionary<ExternMemory, Memory> _externMemoryCache = new();
        private readonly ConcurrentDictionary<ExternGlobal, Global> _externGlobalCache = new();

        internal Function GetCachedExtern(ExternFunc @extern)
        {
            if (!_externFunctionCache.TryGetValue(@extern, out var func))
            {
                func = new Function(this, @extern);
                func = _externFunctionCache.GetOrAdd(@extern, func);
            }

            return func;
        }

        internal Memory GetCachedExtern(ExternMemory @extern)
        {
            if (!_externMemoryCache.TryGetValue(@extern, out var mem))
            {
                mem = new Memory(this, @extern);
                mem = _externMemoryCache.GetOrAdd(@extern, mem);
            }

            return mem;
        }

        internal Global GetCachedExtern(ExternGlobal @extern)
        {
            if (!_externGlobalCache.TryGetValue(@extern, out var global))
            {
                global = new Global(this, @extern);
                global = _externGlobalCache.GetOrAdd(@extern, global);
            }

            return global;
        }
    }
}
