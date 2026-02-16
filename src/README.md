## Installation

You can add a package reference with the [.NET SDK](https://dotnet.microsoft.com/):

```text
$ dotnet add package wasmtime
```

## Introduction

For this introduction, we'll be using a simple WebAssembly module that imports a `hello` function and exports a `run` function:

```wat
(module
  (func $hello (import "" "hello"))
  (func (export "run") (call $hello))
)
```

To use this module from .NET, create a new console project:

```
$ mkdir wasmintro
$ cd wasmintro
$ dotnet new console
```

Next, add a reference to the [Wasmtime package](https://www.nuget.org/packages/Wasmtime):

```
$ dotnet add package wasmtime
```

Replace the contents of `Program.cs` with the following code:

```csharp
using System;
using Wasmtime;

using var engine = new Engine();

using var module = Module.FromText(
    engine,
    "hello",
    "(module (func $hello (import \"\" \"hello\")) (func (export \"run\") (call $hello)))"
);

using var linker = new Linker(engine);
using var store = new Store(engine);

linker.Define(
    "",
    "hello",
    Function.FromCallback(store, () => Console.WriteLine("Hello from C#!"))
);

var instance = linker.Instantiate(store, module);
var run = instance.GetAction("run")!;
run();
```

An `Engine` is created and then a WebAssembly module is loaded from a string in WebAssembly text format.

A `Linker` defines a function called `hello` that simply prints a hello message.

The module is instantiated and the instance's `run` export is invoked.

To run the application, simply use `dotnet`:

```
$ dotnet run
```

This should print `Hello from C#!`.

## Threading Model

Wasmtime's unmanaged C API allows moving a `Store` between threads, but does not
allow using the same `Store` concurrently from multiple threads.

For true parallel execution:

1. Share one `Engine` (and compiled `Module`) across threads.
2. Create one `Store` per worker thread.
3. Coordinate between stores with `SharedMemory` plus host message passing.

`StoreWorker<TState>` can host a dedicated worker `Store` and dispatch work to it:

```csharp
using var config = new Config()
    .WithWasmThreads(true)
    .WithSharedMemory(true);
using var engine = new Engine(config);
using var module = Module.FromText(
    engine,
    "m",
    """
    (module
      (import "env" "mem" (memory 1 1 shared))
      (func (export "worker_entry")))
    """);
using var sharedMemory = new SharedMemory(engine, 1, 1);

using var producerStore = new Store(engine);
using var consumerStore = new Store(engine);
using var producerLinker = new Linker(engine);
using var consumerLinker = new Linker(engine);

producerLinker.Define("env", "mem", sharedMemory, producerStore);
consumerLinker.Define("env", "mem", sharedMemory, consumerStore);

using var worker = new StoreWorker<Action>(
    engine,
    (workerStore, workerLinker) =>
    {
        workerLinker.Define("env", "mem", sharedMemory, workerStore);
        var workerInstance = workerLinker.Instantiate(workerStore, module);
        return workerInstance.GetAction("worker_entry")!;
    });

worker.Invoke((_, workerEntry) => workerEntry());
```
