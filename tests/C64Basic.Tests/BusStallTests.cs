using C64Basic.Core.Machine;
using Xunit;

namespace C64Basic.Tests;

/// <summary>The VIC-II holds the 6502 up cycle by cycle: BA low halts it in its next read cycle, a few writes still go through.</summary>
public class BusStallTests
{
    const int Line = Vic2.CyclesPerLine;
    const int Reads = 0;
    const int BadLine = 51;                                   // 51 & 7 == 3 (the scroll value of $D011 = $1B), inside the display window
    const long BadLineStart = BadLine * (long)Line;

    static Bus Screen(int sprites = 0, int spriteY = 100)
    {
        var bus = new Bus { Seconds = () => 0 };
        bus.Write(0xD011, 0x1B);
        bus.Write(0xD015, (byte)sprites);
        for (int n = 0; n < 8; n++) bus.Write(0xD001 + n * 2, (byte)spriteY);
        return bus;
    }

    static int Stall(Bus bus, long start, int length, int writes = Reads) => bus.Vic.StallCycles(start, length, writes);

    [Fact]
    public void ANormalLineNeverStalls()
    {
        var bus = Screen();
        for (int cycle = 0; cycle < Line; cycle++) Assert.Equal(0, Stall(bus, 52L * Line + cycle, 2));
    }

    [Fact]
    public void WithTheScreenOffThereAreNoBadLines()
    {
        var bus = Screen();
        bus.Write(0xD011, 0x0B);
        Assert.Equal(0, Stall(bus, BadLineStart + 11, 2));
    }

    [Fact]
    public void ABadLineHoldsAReadingProcessorFromCycle12ThroughCycle54()
    {
        var bus = Screen();
        Assert.Equal(43, Stall(bus, BadLineStart + 11, 2));   // the first read is in cycle 12: it waits until 55
        Assert.Equal(43, Stall(bus, BadLineStart + 10, 2));   // the first read is free, the second waits
        Assert.Equal(0, Stall(bus, BadLineStart + 8, 2));     // through before BA goes low
        Assert.Equal(1, Stall(bus, BadLineStart + 53, 2));    // cycle 54 is the last one held
        Assert.Equal(0, Stall(bus, BadLineStart + 54, 2));
    }

    [Fact]
    public void AStoreInTheWarningCyclesGoesThrough()
    {
        var bus = Screen();
        const int store = 1 << 3;                             // STA abs: three reads, then the write
        Assert.Equal(0, Stall(bus, BadLineStart + 8, 4, store));      // the write is in cycle 12, the first one BA is low
        Assert.Equal(43, Stall(bus, BadLineStart + 9, 4, store));     // but here a read comes first in cycle 12
    }

    [Fact]
    public void AnInterruptEntryDoesItsThreePushesInTheWarningThenWaitsForTheVectorRead()
    {
        var bus = Screen();
        const int pushes = 1 << 2 | 1 << 3 | 1 << 4;
        // writes in slots 11, 12, 13 go through; the next cycle is a read and waits until 55
        Assert.Equal(40, Stall(bus, BadLineStart + 9, 7, pushes));
    }

    [Fact]
    public void AWriteAfterTheWarningWaitsToo()
    {
        var bus = Screen();
        const int rmw = 1 << 4 | 1 << 5;                      // INC abs: the old value is written back in cycle 5 and the new one in 6
        // cycles 1-4 are reads: the first of them in slot 11 waits, so both writes follow it
        Assert.Equal(43, Stall(bus, BadLineStart + 8, 6, rmw));
    }

    [Fact]
    public void ASingleSpriteCostsFiveCyclesOfAReadingProcessor()
    {
        var bus = Screen(sprites: 0b1);                       // drawn from line 101, fetched at the end of line 100
        long line100 = 100L * Line;
        Assert.Equal(5, Stall(bus, line100 + 54, 2));         // BA low in cycle 55 (three ahead) until its two cycles are through
        Assert.Equal(0, Stall(bus, line100 + 50, 2));
        Assert.Equal(0, Stall(bus, line100 + 59, 2));
        Assert.Equal(0, Stall(bus, 99L * Line + 54, 2));
    }

    [Fact]
    public void EightSpritesCost19Cycles()
    {
        var bus = Screen(sprites: 0xFF);
        Assert.Equal(19, Stall(bus, 100L * Line + 54, 2));
    }

    [Fact]
    public void TheFetchesRunOverIntoTheNextLine()
    {
        var bus = Screen(sprites: 0xFF);
        Assert.Equal(5, Stall(bus, 101L * Line + 5, 2));      // sprite 7 is fetched until cycle 10 of the next line
    }

    [Fact]
    public void SpritesTwoApartShareOneStretchAndFurtherApartLeaveAGap()
    {
        var near = Screen(sprites: 0b101);                    // 0 and 2: BA low from 55 through 62
        Assert.Equal(9, Stall(near, 100L * Line + 54, 2));
        var far = Screen(sprites: 0b1001);                    // 0 and 3: BA is high in cycle 60 for one cycle
        Assert.Equal(5, Stall(far, 100L * Line + 59, 2));     // the first read is free in the gap, the second waits for sprite 3
        Assert.Equal(10, Stall(far, 100L * Line + 54, 2));
    }

    [Fact]
    public void AStoreInTheWarningBeforeASpriteGoesThrough()
    {
        var bus = Screen(sprites: 0b1);
        Assert.Equal(0, Stall(bus, 100L * Line + 51, 4, 1 << 3));
    }

    // ---------- on a running processor ----------
    static (Cpu6502 Cpu, Bus Bus) Nops()
    {
        var bus = new Bus();
        var cpu = new Cpu6502((ICpuMemory)bus) { Stall = bus.Vic.StallCycles };
        bus.Seconds = () => (cpu.AccessCycle + 0.5) / Cia.ClockHz;
        for (int a = 0x800; a < 0x1000; a++) bus.Ram[a] = 0xEA;
        bus.Ram[0x1000] = 0x4C; bus.Ram[0x1001] = 0x00; bus.Ram[0x1002] = 0x08;      // JMP $0800
        bus.Write(0xD011, 0x1B);
        cpu.PC = 0x800;
        return (cpu, bus);
    }

    static int InstructionsStartedIn(Cpu6502 cpu, long line)
    {
        cpu.Cycles = line * Line - 30;
        while (cpu.Cycles < line * Line) cpu.StepNative();
        int count = 0;
        while (cpu.Cycles < (line + 1) * Line) { cpu.StepNative(); count++; }
        return count;
    }

    [Fact]
    public void AProcessorGetsAboutAThirdOfABadLineAndEverythingOfAnotherLine()
    {
        var (cpu, bus) = Nops();
        int normal = InstructionsStartedIn(cpu, 52);
        Assert.InRange(normal, 31, 32);                       // 63 cycles of 2-cycle NOPs
        int bad = InstructionsStartedIn(cpu, BadLine);
        Assert.InRange(bad, 9, 11);                           // 20 cycles are left: 11 before BA goes low, 8 after cycle 54
        Assert.Equal(0, bus.Vic.StallCycles(52L * Line, 2, 0));
    }

    [Fact]
    public void EightSpritesLeaveAProcessor44CyclesOfALine()
    {
        var (cpu, bus) = Nops();
        bus.Write(0xD011, 0x0B);
        bus.Write(0xD015, 0xFF);
        for (int n = 0; n < 8; n++) bus.Write(0xD001 + n * 2, 100);
        int count = InstructionsStartedIn(cpu, 101);
        Assert.InRange(count, 21, 23);
    }

    [Fact]
    public void TheSchedulerSeesTheWaitInTheNextAccess()
    {
        var (cpu, _) = Nops();
        cpu.Cycles = BadLineStart + 20;                       // BA is low: the next NOP cannot read before cycle 55
        Assert.Equal(BadLineStart + 55, cpu.NextAccessCycle);          // its first read waits until 55, and so does the last cycle
    }
}
