using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace Wasmtime.Tests
{
    public class SharedMemoryExportsFixture : ModuleFixture
    {
        protected override string ModuleFileName => "SharedMemoryExports.wat";

        public override Config GetEngineConfig()
        {
            return new Config()
                .WithMemory64(true)
                .WithWasmThreads(true);
        }
    }

    public class SharedMemoryExportsTests : IClassFixture<SharedMemoryExportsFixture>, IDisposable
    {
        private SharedMemoryExportsFixture Fixture { get; }
        private Store Store { get; }
        private Linker Linker { get; }

        public SharedMemoryExportsTests(SharedMemoryExportsFixture fixture)
        {
            Fixture = fixture;
            Store = new Store(Fixture.Engine);
            Linker = new Linker(Fixture.Engine);
        }

        [Fact]
        public void ItHasSharedMemoryExport()
        {
            var export = Fixture.Module.Exports.OfType<MemoryExport>().Single();
            export.Minimum.Should().Be(1L);
            export.Maximum.Should().Be(2L);
            export.Is64Bit.Should().BeFalse();
            export.IsShared.Should().BeTrue();

            var instance = Linker.Instantiate(Store, Fixture.Module);
            var memory = instance.GetSharedMemory("mem");

            memory.Should().NotBeNull();
            memory!.IsShared.Should().BeTrue();
            memory.Minimum.Should().Be(1L);
            memory.Maximum.Should().Be(2L);
            memory.GetSize().Should().Be(1L);
            memory.Dispose();
        }

        public void Dispose()
        {
            Store.Dispose();
            Linker.Dispose();
        }
    }

    public class SharedMemoryImportFixture : ModuleFixture
    {
        protected override string ModuleFileName => "SharedMemoryImport.wat";

        public override Config GetEngineConfig()
        {
            return new Config()
                .WithMemory64(true)
                .WithWasmThreads(true);
        }
    }

    public class SharedMemoryImportTests : IClassFixture<SharedMemoryImportFixture>, IDisposable
    {
        private SharedMemoryImportFixture Fixture { get; }
        private Store Store { get; }
        private Linker Linker { get; }

        public SharedMemoryImportTests(SharedMemoryImportFixture fixture)
        {
            Fixture = fixture;
            Store = new Store(Fixture.Engine);
            Linker = new Linker(Fixture.Engine);
        }

        [Fact]
        public void ItImportsSharedMemory()
        {
            var import = Fixture.Module.Imports.OfType<MemoryImport>().Single();
            import.Minimum.Should().Be(1L);
            import.Maximum.Should().Be(2L);
            import.Is64Bit.Should().BeFalse();
            import.IsShared.Should().BeTrue();

            using var sharedMemory = new SharedMemory(Fixture.Engine, 1, 2);
            Linker.Define("env", "mem", sharedMemory, Store);

            var instance = Linker.Instantiate(Store, Fixture.Module);

            instance.GetMemory("mem").Should().BeNull();

            var memory = instance.GetSharedMemory("mem");
            memory.Should().NotBeNull();
            memory!.IsShared.Should().BeTrue();
            memory.GetSize().Should().Be(1L);
            memory.Dispose();
        }

        public void Dispose()
        {
            Store.Dispose();
            Linker.Dispose();
        }
    }
}
