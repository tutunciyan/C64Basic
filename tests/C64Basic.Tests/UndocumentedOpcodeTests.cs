using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class UndocumentedOpcodeTests
{
    static (Cpu6502 Cpu, Bus Bus) Run(string code, Action<Bus, Cpu6502>? setup = null)
    {
        var bus = new Bus();
        var cpu = new Cpu6502(bus);
        var bytes = Convert.FromHexString(code.Replace(" ", ""));
        Array.Copy(bytes, 0, bus.Ram, 0x200, bytes.Length);
        setup?.Invoke(bus, cpu);
        cpu.Call(0x200);
        return (cpu, bus);
    }

    static bool Flag(Cpu6502 cpu, int flag) => cpu.GetFlag(flag);

    [Fact]
    public void LaxLoadsAAndX()
    {
        var (cpu, _) = Run("A7 10 60", (b, _) => b.Ram[0x10] = 0x80);          // zero page
        Assert.Equal(0x80, cpu.A);
        Assert.Equal(0x80, cpu.X);
        Assert.True(Flag(cpu, Cpu6502.FlagN));

        (cpu, _) = Run("A0 02 B7 10 60", (b, _) => b.Ram[0x12] = 0x00);          // zero page, Y
        Assert.Equal(0, cpu.X);
        Assert.True(Flag(cpu, Cpu6502.FlagZ));

        (cpu, _) = Run("AF 00 10 60", (b, _) => b.Ram[0x1000] = 0x42);           // absolute
        Assert.Equal(0x42, cpu.A);

        (cpu, _) = Run("A0 03 BF FD 10 60", (b, _) => b.Ram[0x1100] = 0x55);     // absolute, Y
        Assert.Equal(0x55, cpu.X);

        (cpu, _) = Run("A2 02 A3 10 60", (b, _) => { b.Ram[0x12] = 0x00; b.Ram[0x13] = 0x20; b.Ram[0x2000] = 0x66; });   // (zp,X)
        Assert.Equal(0x66, cpu.A);

        (cpu, _) = Run("A0 01 B3 10 60", (b, _) => { b.Ram[0x10] = 0x00; b.Ram[0x11] = 0x20; b.Ram[0x2001] = 0x77; });   // (zp),Y
        Assert.Equal(0x77, cpu.X);
    }

    [Fact]
    public void SaxStoresAAndX()
    {
        var (cpu, bus) = Run("A9 F0 A2 3C 87 20 8F 00 30 60");
        Assert.Equal(0x30, bus.Ram[0x20]);
        Assert.Equal(0x30, bus.Ram[0x3000]);
        Assert.Equal(0xF0, cpu.A);                                              // registers untouched

        (_, bus) = Run("A9 FF A2 0F A0 02 97 20 60");                            // zero page, Y
        Assert.Equal(0x0F, bus.Ram[0x22]);
        (_, bus) = Run("A9 FF A2 03 83 10 60", (b, _) => { b.Ram[0x13] = 0x00; b.Ram[0x14] = 0x30; });   // (zp,X)
        Assert.Equal(0x03, bus.Ram[0x3000]);
    }

    [Fact]
    public void DcpDecrementsThenCompares()
    {
        var (cpu, bus) = Run("A9 04 C7 10 60", (b, _) => b.Ram[0x10] = 5);
        Assert.Equal(4, bus.Ram[0x10]);
        Assert.True(Flag(cpu, Cpu6502.FlagZ));
        Assert.True(Flag(cpu, Cpu6502.FlagC));

        (cpu, bus) = Run("A9 04 C7 10 60", (b, _) => b.Ram[0x10] = 0);           // wraps to $FF: A < $FF
        Assert.Equal(0xFF, bus.Ram[0x10]);
        Assert.False(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void IscIncrementsThenSubtracts()
    {
        var (cpu, bus) = Run("38 A9 10 E7 10 60", (b, _) => b.Ram[0x10] = 5);
        Assert.Equal(6, bus.Ram[0x10]);
        Assert.Equal(0x0A, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void SloShiftsLeftThenOrs()
    {
        var (cpu, bus) = Run("A9 01 07 10 60", (b, _) => b.Ram[0x10] = 0x81);
        Assert.Equal(0x02, bus.Ram[0x10]);
        Assert.Equal(0x03, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void RlaRotatesLeftThenAnds()
    {
        var (cpu, bus) = Run("38 A9 FF 27 10 60", (b, _) => b.Ram[0x10] = 0x80);
        Assert.Equal(0x01, bus.Ram[0x10]);
        Assert.Equal(0x01, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void SreShiftsRightThenXors()
    {
        var (cpu, bus) = Run("A9 FF 47 10 60", (b, _) => b.Ram[0x10] = 0x03);
        Assert.Equal(0x01, bus.Ram[0x10]);
        Assert.Equal(0xFE, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void RraRotatesRightThenAdds()
    {
        var (cpu, bus) = Run("18 A9 10 67 10 60", (b, _) => b.Ram[0x10] = 0x03);   // ROR $03 = $01 carry 1, then ADC with that carry
        Assert.Equal(0x01, bus.Ram[0x10]);
        Assert.Equal(0x12, cpu.A);                                                  // 0x10 + 0x01 + carry
    }

    [Fact]
    public void ReadModifyWriteVariantsHitEveryAddressingMode()
    {
        // SLO with each mode on a known cell: $81 -> $02, A = $03
        string[] programs =
        {
            "A9 01 07 10 60",                 // zp
            "A9 01 17 0F A2 01 60",           // zp,X placeholder, replaced below
        };
        Assert.NotEmpty(programs);

        var (cpu, bus) = Run("A9 01 A2 01 17 0F 60", (b, _) => b.Ram[0x10] = 0x81);      // zp,X
        Assert.Equal(0x03, cpu.A);
        (cpu, bus) = Run("A9 01 0F 00 30 60", (b, _) => b.Ram[0x3000] = 0x81);           // abs
        Assert.Equal(0x03, cpu.A);
        (cpu, bus) = Run("A9 01 A2 01 1F FF 2F 60", (b, _) => b.Ram[0x3000] = 0x81);     // abs,X
        Assert.Equal(0x03, cpu.A);
        (cpu, bus) = Run("A9 01 A0 01 1B FF 2F 60", (b, _) => b.Ram[0x3000] = 0x81);     // abs,Y
        Assert.Equal(0x03, cpu.A);
        (cpu, bus) = Run("A9 01 A2 04 03 10 60", (b, _) => { b.Ram[0x14] = 0x00; b.Ram[0x15] = 0x30; b.Ram[0x3000] = 0x81; });   // (zp,X)
        Assert.Equal(0x03, cpu.A);
        (cpu, bus) = Run("A9 01 A0 01 13 10 60", (b, _) => { b.Ram[0x10] = 0xFF; b.Ram[0x11] = 0x2F; b.Ram[0x3000] = 0x81; });   // (zp),Y
        Assert.Equal(0x03, cpu.A);
        Assert.Equal(0x02, bus.Ram[0x3000]);
    }

    [Fact]
    public void AncCopiesNegativeIntoCarry()
    {
        var (cpu, _) = Run("A9 FF 0B 80 60");
        Assert.Equal(0x80, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
        Assert.True(Flag(cpu, Cpu6502.FlagN));
        (cpu, _) = Run("A9 FF 2B 01 60");
        Assert.False(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void AlrAndsThenShiftsRight()
    {
        var (cpu, _) = Run("A9 FF 4B 0F 60");
        Assert.Equal(0x07, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void ArrAndsThenRotatesRightWithSpecialFlags()
    {
        var (cpu, _) = Run("38 A9 FF 6B FF 60");        // carry in: result $FF
        Assert.Equal(0xFF, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));          // bit 6
        Assert.False(Flag(cpu, Cpu6502.FlagV));         // bit 6 xor bit 5

        (cpu, _) = Run("18 A9 80 6B FF 60");            // carry clear: $80 -> $40
        Assert.Equal(0x40, cpu.A);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
        Assert.True(Flag(cpu, Cpu6502.FlagV));
    }

    [Fact]
    public void AxsSubtractsFromAAndX()
    {
        var (cpu, _) = Run("A9 FF A2 0F CB 05 60");
        Assert.Equal(0x0A, cpu.X);
        Assert.True(Flag(cpu, Cpu6502.FlagC));
        Assert.Equal(0xFF, cpu.A);

        (cpu, _) = Run("A9 FF A2 0F CB 10 60");         // $0F - $10 borrows
        Assert.Equal(0xFF, cpu.X);
        Assert.False(Flag(cpu, Cpu6502.FlagC));
    }

    [Fact]
    public void OpcodeEbIsAnotherSbc()
    {
        var (cpu, _) = Run("38 A9 10 EB 03 60");
        Assert.Equal(0x0D, cpu.A);
    }

    [Theory]
    [InlineData("1A 60", 2 + 6)]                       // implied
    [InlineData("80 FF 60", 2 + 6)]                    // immediate
    [InlineData("04 10 60", 3 + 6)]                    // zero page
    [InlineData("14 10 60", 4 + 6)]                    // zero page, X
    [InlineData("0C 00 10 60", 4 + 6)]                 // absolute
    [InlineData("A2 01 1C FE 10 60", 2 + 4 + 6)]       // absolute, X within a page
    [InlineData("A2 01 1C FF 10 60", 2 + 5 + 6)]       // absolute, X across a page
    public void NopsSkipTheirOperandAndTakeTheRightTime(string code, int cycles)
    {
        var (cpu, _) = Run(code);
        Assert.Equal(CpuStop.Returned, cpu.StopReason);
        Assert.Equal(cycles, cpu.Cycles);
    }

    [Theory]
    [InlineData("07 10 60", 5 + 6)]                    // SLO zp
    [InlineData("17 10 60", 6 + 6)]                    // zp,X
    [InlineData("0F 00 10 60", 6 + 6)]                 // abs
    [InlineData("1F 00 10 60", 7 + 6)]                 // abs,X
    [InlineData("1B 00 10 60", 7 + 6)]                 // abs,Y
    [InlineData("03 10 60", 8 + 6)]                    // (zp,X)
    [InlineData("13 10 60", 8 + 6)]                    // (zp),Y
    [InlineData("A7 10 60", 3 + 6)]                    // LAX zp
    [InlineData("AF 00 10 60", 4 + 6)]                 // LAX abs
    [InlineData("A3 10 60", 6 + 6)]                    // LAX (zp,X)
    [InlineData("87 10 60", 3 + 6)]                    // SAX zp
    [InlineData("8F 00 10 60", 4 + 6)]                 // SAX abs
    [InlineData("0B 00 60", 2 + 6)]                    // ANC #
    public void CycleCountsOfTheCombinedOpcodes(string code, int cycles)
    {
        var (cpu, _) = Run(code, (b, _) => { b.Ram[0x10] = 0; b.Ram[0x11] = 0x30; });
        Assert.Equal(cycles, cpu.Cycles);
    }

    [Fact]
    public void LaxAbsoluteYPaysForACrossedPage()
    {
        var (cpu, _) = Run("A0 01 BF FF 10 60");
        Assert.Equal(2 + 5 + 6, cpu.Cycles);
    }

    [Theory]
    [InlineData("02")]      // JAM
    [InlineData("8B 00")]   // XAA is unstable
    [InlineData("AB 00")]   // LAX #
    [InlineData("93 10")]   // AHX
    [InlineData("9B 00 10")] // TAS
    [InlineData("9C 00 10")] // SHY
    [InlineData("BB 00 10")] // LAS
    public void UnstableOpcodesAndJamsStillStop(string code)
    {
        var (cpu, _) = Run(code);
        Assert.Equal(CpuStop.IllegalOpcode, cpu.StopReason);
    }

    [Fact]
    public void RmwWritesTheOldValueFirst()
    {
        var writes = new List<int>();
        var (_, _) = Run("0F 00 DE 60", (b, _) =>
        {
            b.Map(0xDE00, 16, new Recorder(writes));
        });
        Assert.Equal(new[] { 5, 10 }, writes);          // old value $05, then $05 << 1
    }

    sealed class Recorder : IMemoryMapped
    {
        readonly List<int> _writes;
        byte _value = 5;
        public Recorder(List<int> writes) => _writes = writes;
        public byte Read(int address) => _value;
        public void Write(int address, byte value) { _writes.Add(value); _value = value; }
    }
}
