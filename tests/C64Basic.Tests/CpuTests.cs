using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class CpuTests
{
    const int Origin = 0x200;

    static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    static (Cpu6502 Cpu, Bus Bus) Run(string code, Action<Bus, Cpu6502>? setup = null, int origin = Origin)
    {
        var bus = new Bus();
        var cpu = new Cpu6502(bus);
        var bytes = Hex(code);
        Array.Copy(bytes, 0, bus.Ram, origin, bytes.Length);
        setup?.Invoke(bus, cpu);
        cpu.Call(origin);
        return (cpu, bus);
    }

    static void Load(Bus bus, int address, string code) => Array.Copy(Hex(code), 0, bus.Ram, address, Hex(code).Length);

    [Fact]
    public void LoadsSetFlags()
    {
        var (cpu, _) = Run("A9 80 60");
        Assert.Equal(0x80, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagN));
        Assert.False(cpu.GetFlag(Cpu6502.FlagZ));
        (cpu, _) = Run("A9 00 60");
        Assert.True(cpu.GetFlag(Cpu6502.FlagZ));
    }

    [Fact]
    public void TransfersAndIncrements()
    {
        var (cpu, _) = Run("A9 05 AA A8 E8 C8 C8 60");
        Assert.Equal(6, cpu.X);
        Assert.Equal(7, cpu.Y);
        Assert.Equal(5, cpu.A);
    }

    [Fact]
    public void StackPointerTransfers()
    {
        var (cpu, _) = Run("BA 60");
        Assert.Equal(0xFD, cpu.X);   // the return address occupies the top two bytes
    }

    [Fact]
    public void AdcSetsOverflowAndCarry()
    {
        var (cpu, _) = Run("18 A9 50 69 50 60");
        Assert.Equal(0xA0, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagV));
        Assert.True(cpu.GetFlag(Cpu6502.FlagN));
        Assert.False(cpu.GetFlag(Cpu6502.FlagC));

        (cpu, _) = Run("18 A9 FF 69 01 60");
        Assert.Equal(0, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));
        Assert.True(cpu.GetFlag(Cpu6502.FlagZ));
        Assert.False(cpu.GetFlag(Cpu6502.FlagV));
    }

    [Fact]
    public void SbcBorrowsAndOverflows()
    {
        var (cpu, _) = Run("38 A9 50 E9 F0 60");
        Assert.Equal(0x60, cpu.A);
        Assert.False(cpu.GetFlag(Cpu6502.FlagC));
        Assert.False(cpu.GetFlag(Cpu6502.FlagV));

        (cpu, _) = Run("38 A9 80 E9 01 60");
        Assert.Equal(0x7F, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));
        Assert.True(cpu.GetFlag(Cpu6502.FlagV));
    }

    [Fact]
    public void DecimalModeAdds()
    {
        var (cpu, _) = Run("F8 18 A9 19 69 28 60");
        Assert.Equal(0x47, cpu.A);

        (cpu, _) = Run("F8 18 A9 99 69 01 60");
        Assert.Equal(0x00, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));

        (cpu, _) = Run("F8 38 A9 58 69 46 60"); // 58 + 46 + 1 = 105
        Assert.Equal(0x05, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));
    }

    [Fact]
    public void DecimalModeSubtracts()
    {
        var (cpu, _) = Run("F8 38 A9 46 E9 12 60");
        Assert.Equal(0x34, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));

        (cpu, _) = Run("F8 38 A9 00 E9 01 60");
        Assert.Equal(0x99, cpu.A);
        Assert.False(cpu.GetFlag(Cpu6502.FlagC));
    }

    [Fact]
    public void LogicalOperations()
    {
        var (cpu, _) = Run("A9 F0 29 3C 09 01 49 FF 60"); // AND #$3C -> $30, ORA #1 -> $31, EOR #$FF -> $CE
        Assert.Equal(0xCE, cpu.A);
    }

    [Fact]
    public void ComparesSetCarryAndZero()
    {
        var (cpu, _) = Run("A9 10 C9 10 60");
        Assert.True(cpu.GetFlag(Cpu6502.FlagZ));
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));
        (cpu, _) = Run("A9 10 C9 20 60");
        Assert.False(cpu.GetFlag(Cpu6502.FlagC));
        Assert.True(cpu.GetFlag(Cpu6502.FlagN));
        (cpu, _) = Run("A2 05 E0 03 60");
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));
        (cpu, _) = Run("A0 05 C0 06 60");
        Assert.False(cpu.GetFlag(Cpu6502.FlagC));
    }

    [Fact]
    public void ShiftsAndRotates()
    {
        var (cpu, _) = Run("A9 81 0A 60");
        Assert.Equal(0x02, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));

        (cpu, _) = Run("38 A9 80 2A 60");
        Assert.Equal(0x01, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));

        (cpu, _) = Run("38 A9 01 6A 60");
        Assert.Equal(0x80, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));

        (cpu, _) = Run("A9 03 4A 60");
        Assert.Equal(0x01, cpu.A);
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));

        var (_, bus) = Run("06 10 38 26 10 60", (b, _) => b.Ram[0x10] = 0x40);
        Assert.Equal(0x01, bus.Ram[0x10]);   // ASL gives $80, ROL with carry set gives $01
    }

    [Fact]
    public void MemoryIncrementAndDecrement()
    {
        var (_, bus) = Run("E6 20 E6 20 C6 21 60", (b, _) => b.Ram[0x21] = 0);
        Assert.Equal(2, bus.Ram[0x20]);
        Assert.Equal(0xFF, bus.Ram[0x21]);
    }

    [Fact]
    public void StoresReachMemory()
    {
        var (_, bus) = Run("A9 7F 85 20 8D 00 03 A2 11 86 22 A0 22 84 23 60");
        Assert.Equal(0x7F, bus.Ram[0x20]);
        Assert.Equal(0x7F, bus.Ram[0x300]);
        Assert.Equal(0x11, bus.Ram[0x22]);
        Assert.Equal(0x22, bus.Ram[0x23]);
    }

    [Fact]
    public void ZeroPageIndexingWraps()
    {
        var (cpu, _) = Run("A2 10 B5 F8 60", (b, _) => b.Ram[0x08] = 0x99);   // $F8 + $10 wraps to $08
        Assert.Equal(0x99, cpu.A);
    }

    [Fact]
    public void IndexedAndIndirectAddressing()
    {
        var (cpu, _) = Run("A2 05 BD 00 10 60", (b, _) => b.Ram[0x1005] = 0x77);
        Assert.Equal(0x77, cpu.A);
        (cpu, _) = Run("A0 03 B1 10 60", (b, _) => { b.Ram[0x10] = 0; b.Ram[0x11] = 0x20; b.Ram[0x2003] = 0x66; });
        Assert.Equal(0x66, cpu.A);
        (cpu, _) = Run("A2 04 A1 10 60", (b, _) => { b.Ram[0x14] = 0; b.Ram[0x15] = 0x30; b.Ram[0x3000] = 0x55; });
        Assert.Equal(0x55, cpu.A);
        (cpu, _) = Run("A0 02 B9 FE 10 60", (b, _) => b.Ram[0x1100] = 0x44);
        Assert.Equal(0x44, cpu.A);
        (cpu, _) = Run("A0 02 B6 20 60", (b, _) => { });   // LDX zp,Y
        Assert.Equal(0, cpu.X);
    }

    [Fact]
    public void StoreThroughIndirectY()
    {
        var (_, bus) = Run("A9 5A A0 01 91 10 60", (b, _) => { b.Ram[0x10] = 0x00; b.Ram[0x11] = 0x30; });
        Assert.Equal(0x5A, bus.Ram[0x3001]);
    }

    [Fact]
    public void JsrAndRts()
    {
        var (cpu, _) = Run("20 06 02 A2 07 60 A9 09 60");
        Assert.Equal(9, cpu.A);
        Assert.Equal(7, cpu.X);
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
        Assert.Equal(0xFF, cpu.SP);
    }

    [Fact]
    public void StackPushAndPull()
    {
        var (cpu, bus) = Run("A9 42 48 A9 00 68 60");
        Assert.Equal(0x42, cpu.A);
        Assert.Equal(0x42, bus.Ram[0x1FD]);
    }

    [Fact]
    public void PhpPushesBreakAndUnusedBitsAndPlpIgnoresBreak()
    {
        var (cpu, bus) = Run("38 08 18 28 60");
        Assert.Equal(0x31, bus.Ram[0x1FD]);   // carry, unused, break
        Assert.True(cpu.GetFlag(Cpu6502.FlagC));
        Assert.False(cpu.GetFlag(Cpu6502.FlagB));
    }

    [Fact]
    public void JumpIndirectDoesNotCarryAcrossPages()
    {
        var (cpu, _) = Run("6C FF 12", (b, _) =>
        {
            b.Ram[0x12FF] = 0x00; b.Ram[0x1200] = 0x03; b.Ram[0x1300] = 0x04;
            Load(b, 0x300, "A9 11 60");
            Load(b, 0x400, "A9 22 60");
        });
        Assert.Equal(0x11, cpu.A);
    }

    [Fact]
    public void BitTestsMemory()
    {
        var (cpu, _) = Run("A9 01 24 10 60", (b, _) => b.Ram[0x10] = 0xC0);
        Assert.True(cpu.GetFlag(Cpu6502.FlagZ));
        Assert.True(cpu.GetFlag(Cpu6502.FlagN));
        Assert.True(cpu.GetFlag(Cpu6502.FlagV));
    }

    [Fact]
    public void BranchesFollowFlags()
    {
        // count down X from 5, adding 2 to A each time
        var (cpu, _) = Run("A2 05 A9 00 18 69 02 CA D0 FA 60");
        Assert.Equal(10, cpu.A);
        Assert.Equal(0, cpu.X);
        // backward and forward branch on carry
        (cpu, _) = Run("38 B0 02 A9 01 A2 07 60");
        Assert.Equal(0, cpu.A);
        Assert.Equal(7, cpu.X);
    }

    [Fact]
    public void LoopCycleCountIsExact()
    {
        var (cpu, _) = Run("A2 05 CA D0 FD 60");
        // LDX 2 + 4 x (DEX 2 + BNE taken 3) + (DEX 2 + BNE not taken 2) + RTS 6
        Assert.Equal(2 + 4 * 5 + 4 + 6, cpu.Cycles);
    }

    [Fact]
    public void IndexedReadsPayForCrossingAPage()
    {
        var (cpu, _) = Run("A2 01 BD FE 10 60");
        Assert.Equal(2 + 4 + 6, cpu.Cycles);
        (cpu, _) = Run("A2 01 BD FF 10 60");
        Assert.Equal(2 + 5 + 6, cpu.Cycles);
        (cpu, _) = Run("A2 01 9D FF 10 60");   // stores always take 5
        Assert.Equal(2 + 5 + 6, cpu.Cycles);
    }

    [Fact]
    public void TakenBranchesPayForCrossingAPage()
    {
        var (cpu, _) = Run("A9 00 F0 00 60");
        Assert.Equal(2 + 3 + 6, cpu.Cycles);
        (cpu, _) = Run("A9 00 F0 08", (b, _) => Load(b, 0x304, "60"), 0x2F8);
        Assert.Equal(2 + 4 + 6, cpu.Cycles);
    }

    sealed class Recorder : IMemoryMapped
    {
        public readonly List<int> Writes = new();
        byte _value = 5;
        public byte Read(int address) => _value;
        public void Write(int address, byte value) { Writes.Add(value); _value = value; }
    }

    [Fact]
    public void ReadModifyWriteWritesTheOldValueFirst()
    {
        var chip = new Recorder();
        Run("EE 00 DE 60", (b, _) => b.Map(0xDE00, 16, chip));
        Assert.Equal(new[] { 5, 6 }, chip.Writes);
    }

    [Fact]
    public void BrkWithDefaultVectorReturnsToBasic()
    {
        var (cpu, _) = Run("A2 03 00");
        Assert.Equal(CpuStop.Break, cpu.StopReason);
        Assert.Equal(3, cpu.X);
    }

    [Fact]
    public void BrkUsesTheHardwareVectorWithRomBankedOut()
    {
        var (cpu, bus) = Run("00 EA A2 07 60", (b, _) =>
        {
            b.Ram[1] = 0x35;                       // RAM at $E000, I/O visible
            b.Ram[0xFFFE] = 0x00; b.Ram[0xFFFF] = 0x03;
            Load(b, 0x300, "A9 33 40");
        });
        Assert.Equal(0x33, cpu.A);
        Assert.Equal(7, cpu.X);                    // RTI came back after the padding byte
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
    }

    [Fact]
    public void BrkThroughKernalVectorCanBeHooked()
    {
        var (cpu, _) = Run("00 EA 60", (b, _) =>
        {
            b.Ram[0x316] = 0x00; b.Ram[0x317] = 0x03;
            Load(b, 0x300, "68 A8 68 AA 68 A9 44 40"); // pulls Y, X, A like the ROM's tail, then LDA #$44, RTI
        });
        Assert.Equal(0x44, cpu.A);
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
    }

    [Fact]
    public void UndocumentedOpcodesStop()
    {
        var (cpu, _) = Run("A2 01 02");
        Assert.Equal(CpuStop.IllegalOpcode, cpu.StopReason);
        Assert.Equal(1, cpu.X);
        Assert.Equal(0x202, cpu.PC);
    }

    [Fact]
    public void CallingRomWithoutAnEmulationStops()
    {
        var (cpu, _) = Run("20 00 FF 60");
        Assert.Equal(CpuStop.NoRom, cpu.StopReason);
    }

    [Fact]
    public void RomIsRamOnceBankedOut()
    {
        var (cpu, _) = Run("78 A9 35 85 01 20 00 E0 60", (b, _) => Load(b, 0xE000, "A2 09 60"));
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
        Assert.Equal(9, cpu.X);
    }

    [Fact]
    public void TrapsReplaceRomRoutines()
    {
        var (cpu, _) = Run("20 D2 FF 60", (b, c) => c.Traps[0xFFD2] = t => { t.X = 9; return TrapResult.Return; });
        Assert.Equal(9, cpu.X);
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
    }

    [Fact]
    public void TrapCanEndTheCall()
    {
        var (cpu, _) = Run("20 74 A4 A2 01 60", (b, c) => c.Traps[0xA474] = t => TrapResult.Stop);
        Assert.Equal(CpuStop.Terminated, cpu.StopReason);
        Assert.Equal(0, cpu.X);
    }

    [Fact]
    public void TickCanInterruptARunawayLoop()
    {
        int ticks = 0;
        var (cpu, _) = Run("4C 00 02", (b, c) => c.Tick = cycles => ++ticks == 3);
        Assert.Equal(CpuStop.Interrupted, cpu.StopReason);
        Assert.True(cpu.Cycles >= 3 * 1024);
    }

    [Fact]
    public void CiaTimerInterruptRunsTheUserHandler()
    {
        double now = 0;
        var (cpu, bus) = Run("58 A5 FB F0 FC 60", (b, c) =>
        {
            b.Seconds = () => now;
            b.Ram[0x314] = 0x00; b.Ram[0x315] = 0x03;
            // INC $FB, acknowledge the CIA, then pull Y, X, A and RTI like the ROM tail
            Load(b, 0x300, "E6 FB AD 0D DC 68 A8 68 AA 68 40");
            c.Tick = _ => { now += 0.001; return now > 1; };
        });
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
        Assert.Equal(1, bus.Ram[0xFB]);
        Assert.True(now < 0.1);
    }

    [Fact]
    public void RasterInterruptRunsTheHandlerAndIsAcknowledged()
    {
        double now = 0;
        var (cpu, bus) = Run("78 A9 7F 8D 0D DC A9 40 8D 12 D0 A9 01 8D 1A D0 58 A5 FB F0 FC 60", (b, c) =>
        {
            b.Seconds = () => now;
            b.Ram[0x314] = 0x00; b.Ram[0x315] = 0x03;
            // INC $FB, ack VIC with ASL $D019 (the RMW dummy write clears the flag), pull Y, X, A, RTI
            Load(b, 0x300, "E6 FB 0E 19 D0 68 A8 68 AA 68 40");
            c.Tick = _ => { now += 0.001; return now > 1; };
        });
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
        Assert.Equal(1, bus.Ram[0xFB]);
    }

    [Fact]
    public void StockIrqHandlerIsEmulated()
    {
        double now = 0;
        int jiffies = 0;
        var (cpu, _) = Run("58 4C 01 02", (b, c) =>
        {
            b.Seconds = () => now;
            c.SystemIrq = () => jiffies++;
            c.Tick = _ => { now += 0.001; return jiffies >= 2; };
        });
        Assert.Equal(CpuStop.Interrupted, cpu.StopReason);
        Assert.Equal(2, jiffies);
    }

    [Fact]
    public void InterruptsAreIgnoredWhileTheIFlagIsSet()
    {
        double now = 0;
        int jiffies = 0;
        Run("78 4C 01 02", (b, c) =>
        {
            b.Seconds = () => now;
            c.SystemIrq = () => jiffies++;
            c.Tick = _ => { now += 0.001; return now > 0.1; };
        });
        Assert.Equal(0, jiffies);
    }

    [Fact]
    public void FunctionalTestSuite()
    {
        // Klaus Dormann's 6502 functional test is GPL; point C64_6502_FUNCTIONAL_TEST at 6502_functional_test.bin to run it.
        string? path = Environment.GetEnvironmentVariable("C64_6502_FUNCTIONAL_TEST");
        if (path == null || !File.Exists(path)) return;

        var bus = new Bus();
        var image = File.ReadAllBytes(path);
        Array.Copy(image, bus.Ram, Math.Min(image.Length, 65536));
        var cpu = new Cpu6502(bus) { PC = 0x400 };

        const int success = 0x3469;
        for (long steps = 0; steps < 200_000_000; steps++)
        {
            int before = cpu.PC;
            cpu.Step();
            if (cpu.StopReason != CpuStop.None) Assert.Fail($"stopped {cpu.StopReason} at {cpu.PC:X4}");
            if (cpu.PC == before)
            {
                Assert.Equal(success, cpu.PC);
                return;
            }
        }
        Assert.Fail("test did not finish");
    }
}
