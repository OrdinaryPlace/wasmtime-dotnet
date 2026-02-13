using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Wasmtime
{
    /// <summary>
    /// Represents an asynchronous in-flight invocation of a WebAssembly function.
    /// </summary>
    /// <remarks>
    /// The future must be disposed when no longer needed. While a future is alive, the associated
    /// <see cref="Store"/> cannot be used for other operations.
    /// </remarks>
    public sealed class FunctionCallFuture : IDisposable
    {
        internal unsafe FunctionCallFuture(Function function, ReadOnlyMemory<ValueBox> arguments)
        {
            if (function is null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            if (function.IsNull)
            {
                throw new InvalidOperationException("Cannot invoke a null function reference.");
            }

            store = function.store ?? throw new ArgumentNullException(nameof(function));
            this.function = function;

            if (arguments.Length != function.Parameters.Count)
            {
                throw new WasmtimeException($"Argument mismatch when invoking function: requires {function.Parameters.Count} but was given {arguments.Length}.");
            }

            argumentValues = CreateArgumentValues(arguments.Span);
            resultValues = new Value[function.Results.Count];
            asyncExecutionLease = store.EnterAsyncExecutionLease();

            try
            {
                argsBuffer = AllocateValueBuffer(argumentValues.Length);
                resultsBuffer = AllocateValueBuffer(resultValues.Length);
                trapSlot = Marshal.AllocHGlobal(IntPtr.Size);
                errorSlot = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(trapSlot, IntPtr.Zero);
                Marshal.WriteIntPtr(errorSlot, IntPtr.Zero);

                CopyValuesToNative(argumentValues, argsBuffer);
                CopyValuesToNative(resultValues, resultsBuffer);

                using var executionScope = store.EnterExecutionScope(allowDuringAsyncExecution: true);
                var context = store.ContextForAsyncExecution;

                futureHandle = Function.Native.wasmtime_func_call_async(
                    context.handle,
                    in this.function.func,
                    (Value*)argsBuffer,
                    (nuint)argumentValues.Length,
                    (Value*)resultsBuffer,
                    (nuint)resultValues.Length,
                    trapSlot,
                    errorSlot
                );

                GC.KeepAlive(store);

                if (futureHandle == IntPtr.Zero)
                {
                    ThrowIfErrorOrTrap();
                    throw new WasmtimeException("Failed to create an async call future.");
                }
            }
            catch
            {
                try
                {
                    Dispose();
                }
                catch
                {
                    // Preserve the original exception.
                }

                throw;
            }
        }

        /// <summary>
        /// Returns whether this call-future has completed execution.
        /// </summary>
        public bool IsCompleted
        {
            get
            {
                lock (sync)
                {
                    return isCompleted;
                }
            }
        }

        /// <summary>
        /// Polls this call-future.
        /// </summary>
        /// <returns>True if the invocation has completed; false if it yielded and can be resumed by polling again.</returns>
        public bool Poll()
        {
            lock (sync)
            {
                ThrowIfDisposed();

                if (isCompleted)
                {
                    return true;
                }

                using var executionScope = store.EnterExecutionScope(allowDuringAsyncExecution: true);
                isCompleted = Function.Native.wasmtime_call_future_poll(futureHandle);
                GC.KeepAlive(store);
                return isCompleted;
            }
        }

        /// <summary>
        /// Polls this call-future until completion.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token used between yield points.</param>
        /// <returns>
        ///   Returns null if the function has no return value.
        ///   Returns the value if the function returns a single value.
        ///   Returns an array of values if the function returns more than one value.
        /// </returns>
        public async Task<object?> CompleteAsync(CancellationToken cancellationToken = default)
        {
            while (!Poll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return GetResult();
        }

        /// <summary>
        /// Gets the completed result of this call-future.
        /// </summary>
        /// <returns>
        ///   Returns null if the function has no return value.
        ///   Returns the value if the function returns a single value.
        ///   Returns an array of values if the function returns more than one value.
        /// </returns>
        public object? GetResult()
        {
            lock (sync)
            {
                ThrowIfDisposed();

                if (!isCompleted)
                {
                    throw new InvalidOperationException("The asynchronous call has not completed yet.");
                }

                EvaluateCompletionResultIfNeeded();
                completionException?.Throw();
                return completionResult;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;

                try
                {
                    if (futureHandle != IntPtr.Zero)
                    {
                        Function.Native.wasmtime_call_future_delete(futureHandle);
                        futureHandle = IntPtr.Zero;
                    }

                    ReleaseResultValues(allowDuringAsyncExecution: true);
                    ReleaseArgumentValues(allowDuringAsyncExecution: true);
                    DeletePendingErrorOrTrap();
                }
                finally
                {
                    if (trapSlot != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(trapSlot);
                        trapSlot = IntPtr.Zero;
                    }

                    if (errorSlot != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(errorSlot);
                        errorSlot = IntPtr.Zero;
                    }

                    if (resultsBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(resultsBuffer);
                        resultsBuffer = IntPtr.Zero;
                    }

                    if (argsBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(argsBuffer);
                        argsBuffer = IntPtr.Zero;
                    }

                    asyncExecutionLease?.Dispose();
                    asyncExecutionLease = null;
                }
            }
        }

        private void EvaluateCompletionResultIfNeeded()
        {
            if (completionEvaluated)
            {
                return;
            }

            try
            {
                ThrowIfErrorOrTrap();
                LoadResultValues();
                completionResult = CreateManagedResult();
            }
            catch (Exception ex)
            {
                completionException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                ReleaseResultValues(allowDuringAsyncExecution: true);
                ReleaseArgumentValues(allowDuringAsyncExecution: true);
                completionEvaluated = true;
            }
        }

        private object? CreateManagedResult()
        {
            if (function.Results.Count == 0)
            {
                return null;
            }

            if (function.Results.Count == 1)
            {
                return resultValues[0].ToObject(store, allowDuringAsyncExecution: true);
            }

            var result = new object?[function.Results.Count];

            for (int i = 0; i < function.Results.Count; i++)
            {
                result[i] = resultValues[i].ToObject(store, allowDuringAsyncExecution: true);
            }

            return result;
        }

        private void ThrowIfErrorOrTrap()
        {
            var error = ReadAndClearOwnedPointer(errorSlot);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }

            var trap = ReadAndClearOwnedPointer(trapSlot);
            if (trap != IntPtr.Zero)
            {
                throw TrapException.FromOwnedTrap(trap);
            }
        }

        private void DeletePendingErrorOrTrap()
        {
            var error = ReadAndClearOwnedPointer(errorSlot);
            if (error != IntPtr.Zero)
            {
                WasmtimeException.Native.wasmtime_error_delete(error);
            }

            var trap = ReadAndClearOwnedPointer(trapSlot);
            if (trap != IntPtr.Zero)
            {
                TrapException.Native.wasm_trap_delete(trap);
            }
        }

        private static IntPtr ReadAndClearOwnedPointer(IntPtr slot)
        {
            if (slot == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var value = Marshal.ReadIntPtr(slot);
            if (value != IntPtr.Zero)
            {
                Marshal.WriteIntPtr(slot, IntPtr.Zero);
            }

            return value;
        }

        private unsafe void LoadResultValues()
        {
            if (resultValuesLoaded)
            {
                return;
            }

            if (resultValues.Length == 0 || resultsBuffer == IntPtr.Zero)
            {
                resultValuesLoaded = true;
                return;
            }

            CopyValuesFromNative(resultsBuffer, resultValues);
            resultValuesLoaded = true;
        }

        private void ReleaseArgumentValues(bool allowDuringAsyncExecution)
        {
            if (argumentValuesReleased)
            {
                return;
            }

            for (int i = 0; i < argumentValues.Length; i++)
            {
                argumentValues[i].Release(store, allowDuringAsyncExecution);
            }

            argumentValuesReleased = true;
        }

        private void ReleaseResultValues(bool allowDuringAsyncExecution)
        {
            if (resultValuesReleased)
            {
                return;
            }

            LoadResultValues();

            for (int i = 0; i < resultValues.Length; i++)
            {
                resultValues[i].Release(store, allowDuringAsyncExecution);
            }

            resultValuesReleased = true;
        }

        private Value[] CreateArgumentValues(ReadOnlySpan<ValueBox> arguments)
        {
            var values = new Value[arguments.Length];

            for (int i = 0; i < arguments.Length; i++)
            {
                try
                {
                    values[i] = arguments[i].ToValue(store, function.Parameters[i]);
                }
                catch
                {
                    for (int releaseIndex = 0; releaseIndex < i; releaseIndex++)
                    {
                        values[releaseIndex].Release(store);
                    }

                    throw;
                }
            }

            return values;
        }

        private unsafe static IntPtr AllocateValueBuffer(int valueCount)
        {
            if (valueCount == 0)
            {
                return IntPtr.Zero;
            }

            var byteCount = checked(sizeof(Value) * valueCount);
            var buffer = Marshal.AllocHGlobal(byteCount);
            var span = new Span<byte>((void*)buffer, byteCount);
            span.Clear();
            return buffer;
        }

        private unsafe static void CopyValuesToNative(ReadOnlySpan<Value> source, IntPtr destination)
        {
            if (source.Length == 0)
            {
                return;
            }

            var byteCount = checked(sizeof(Value) * source.Length);

            fixed (Value* sourcePtr = source)
            {
                Buffer.MemoryCopy(sourcePtr, (void*)destination, byteCount, byteCount);
            }
        }

        private unsafe static void CopyValuesFromNative(IntPtr source, Span<Value> destination)
        {
            if (destination.Length == 0)
            {
                return;
            }

            var byteCount = checked(sizeof(Value) * destination.Length);

            fixed (Value* destinationPtr = destination)
            {
                Buffer.MemoryCopy((void*)source, destinationPtr, byteCount, byteCount);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(typeof(FunctionCallFuture).FullName);
            }
        }

        private readonly object sync = new();
        private readonly Store store;
        private readonly Function function;
        private readonly Value[] argumentValues;
        private readonly Value[] resultValues;

        private Store.AsyncExecutionLease? asyncExecutionLease;
        private IntPtr argsBuffer;
        private IntPtr resultsBuffer;
        private IntPtr trapSlot;
        private IntPtr errorSlot;
        private IntPtr futureHandle;

        private bool isCompleted;
        private bool disposed;
        private bool completionEvaluated;
        private bool resultValuesLoaded;
        private bool argumentValuesReleased;
        private bool resultValuesReleased;
        private object? completionResult;
        private ExceptionDispatchInfo? completionException;
    }
}
