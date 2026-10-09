using C64Basic.Core.Machine;

namespace C64Basic.Tests;

/// <summary>Klaus Dormann's 6502 functional test (GPL, so fetched into <c>roms/cputests</c> by CI and by hand, not kept in git).</summary>
public class CpuFunctionalTests
{
    sealed class Flat : ICpuMemory
    {
        public readonly byte[] Ram = new byte[65536];
        public int Read(int address) => Ram[address];
        public void Write(int address, byte value) => Ram[address] = value;
    }

    static string? Find(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "roms", "cputests", name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    sealed class CpuTestFactAttribute : FactAttribute
    {
        public CpuTestFactAttribute()
        {
            if (Find("6502_functional_test.bin") == null) Skip = "needs roms/cputests/6502_functional_test.bin (see the CI workflow for where it comes from)";
        }
    }

    [CpuTestFact]
    public void TheNativeProcessorPassesTheFunctionalTestForTheDocumentedOpcodes()
    {
        var memory = new Flat();
        File.ReadAllBytes(Find("6502_functional_test.bin")!).CopyTo(memory.Ram, 0);
        var cpu = new Cpu6502(memory) { PC = 0x400 };
        int last = -1;
        long guard = 120_000_000;
        while (cpu.Cycles < guard)
        {
            last = cpu.PC;
            cpu.StepNative();
            Assert.Equal(CpuStop.None, cpu.StopReason);
            if (cpu.PC == last) break;                       // the test ends in a JMP * : success at $3469, any other one is a failed check
        }
        Assert.True(cpu.PC == last, "the test did not finish");
        Assert.Equal(0x3469, cpu.PC);
    }
}
