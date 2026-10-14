using C64Basic.Core.Machine;

namespace C64Basic.Tests;

/// <summary>Runs a routine on a processor the way a JSR would: until it returns to <see cref="ReturnAddress"/> or stops.</summary>
static class CpuCall
{
    public const int ReturnAddress = 0xFFFF;

    public static CpuStop Call(this Cpu6502 cpu, int address, int maxSteps = 5_000_000)
    {
        cpu.Push((ReturnAddress - 1) >> 8);
        cpu.Push((ReturnAddress - 1) & 0xFF);
        cpu.PC = address & 0xFFFF;
        for (int i = 0; i < maxSteps && cpu.PC != ReturnAddress && cpu.StopReason == CpuStop.None; i++) cpu.Step();
        return cpu.StopReason;
    }

    public static bool Returned(this Cpu6502 cpu) => cpu.PC == ReturnAddress && cpu.StopReason == CpuStop.None;
}
