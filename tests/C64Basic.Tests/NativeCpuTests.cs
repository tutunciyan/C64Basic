using C64Basic.Core.Machine;

namespace C64Basic.Tests;

/// <summary>The processor without the C64: plain 64K, real vectors, interrupt lines, and two of them in lockstep.</summary>
public class NativeCpuTests
{
    /// <summary>64K of RAM that remembers the cycle of every access, and can share one byte at $D000 with another processor.</summary>
    sealed class Ram : ICpuMemory
    {
        public readonly byte[] Data = new byte[65536];
        public readonly List<long> AccessCycles = new();
        public Cpu6502? Cpu;
        readonly byte[]? _mailbox;

        public Ram(byte[]? mailbox = null) => _mailbox = mailbox;

        public int Read(int address)
        {
            AccessCycles.Add(Cpu?.AccessCycle ?? 0);
            return address == 0xD000 && _mailbox != null ? _mailbox[0] : Data[address];
        }

        public void Write(int address, byte value)
        {
            AccessCycles.Add(Cpu?.AccessCycle ?? 0);
            if (address == 0xD000 && _mailbox != null) _mailbox[0] = value; else Data[address] = value;
        }

        public void Load(int address, string hex)
        {
            var bytes = Convert.FromHexString(hex.Replace(" ", ""));
            Array.Copy(bytes, 0, Data, address, bytes.Length);
        }

        public void Vector(int at, int target) { Data[at] = (byte)target; Data[at + 1] = (byte)(target >> 8); }
    }

    static (Cpu6502 Cpu, Ram Ram) Machine(string code, int origin = 0x0600, byte[]? mailbox = null)
    {
        var ram = new Ram(mailbox);
        ram.Load(origin, code);
        ram.Vector(0xFFFC, origin);
        var cpu = new Cpu6502(ram);
        ram.Cpu = cpu;
        cpu.Reset();
        return (cpu, ram);
    }

    [Fact]
    public void ResetReadsTheVectorAndMasksInterrupts()
    {
        var (cpu, _) = Machine("EA");
        Assert.Equal(0x0600, cpu.PC);
        Assert.Equal(0xFD, cpu.SP);
        Assert.True(cpu.GetFlag(Cpu6502.FlagI));
        Assert.True(cpu.Native);
    }

    [Fact]
    public void RunsInstructionsWithoutTraps()
    {
        var (cpu, ram) = Machine("A9 05 8D 00 03 E8 4C 00 06");   // LDA #5 : STA $0300 : INX : JMP $0600
        cpu.Traps[0x0600] = c => throw new InvalidOperationException("traps are not used in native mode");
        for (int i = 0; i < 4; i++) cpu.StepNative();
        Assert.Equal(5, ram.Data[0x300]);
        Assert.Equal(1, cpu.X);
    }

    [Fact]
    public void RomAddressesAreJustMemory()
    {
        // an interpreter-mode CPU would stop on a jump into $E000; a native one executes what is there
        var (cpu, ram) = Machine("4C 00 E0");
        ram.Load(0xE000, "A2 07");
        cpu.StepNative(); cpu.StepNative();
        Assert.Equal(7, cpu.X);
        Assert.Equal(CpuStop.None, cpu.StopReason);
    }

    [Fact]
    public void IrqIsTakenOnlyWhileTheLineIsLowAndInterruptsAreEnabled()
    {
        var (cpu, ram) = Machine("58 EA EA EA");              // CLI NOP NOP NOP
        bool line = true;
        cpu.IrqLine = () => line;
        ram.Vector(0xFFFE, 0x0700);
        ram.Load(0x0700, "E8 40");                             // INX RTI
        cpu.StepNative();                                      // I is set: the IRQ waits, CLI runs
        Assert.Equal(0x0601, cpu.PC);
        cpu.StepNative();                                      // now the interrupt is taken
        Assert.Equal(0x0700, cpu.PC);
        Assert.True(cpu.GetFlag(Cpu6502.FlagI));
        Assert.Equal(0xFA, cpu.SP);
        Assert.Equal(0x0601, ram.Data[0x1FD] << 8 | ram.Data[0x1FC]);
        Assert.Equal(0, ram.Data[0x1FB] & Cpu6502.FlagB);      // pushed without B
        line = false;
        cpu.StepNative(); cpu.StepNative();                    // INX, RTI
        Assert.Equal(1, cpu.X);
        Assert.Equal(0x0601, cpu.PC);
        Assert.False(cpu.GetFlag(Cpu6502.FlagI));
    }

    [Fact]
    public void NmiIsEdgeTriggeredAndIgnoresTheMask()
    {
        var (cpu, ram) = Machine("EA EA EA EA");
        bool line = false;
        cpu.NmiLine = () => line;
        ram.Vector(0xFFFA, 0x0800);
        ram.Load(0x0800, "E8 40");
        line = true;
        cpu.StepNative();
        Assert.Equal(0x0800, cpu.PC);
        cpu.StepNative(); cpu.StepNative();                    // INX, RTI: the line is still low, but there is no second NMI
        Assert.Equal(0x0600, cpu.PC);
        cpu.StepNative();
        Assert.Equal(0x0601, cpu.PC);
        line = false; cpu.StepNative();
        line = true; cpu.StepNative();                         // a new edge
        Assert.Equal(0x0800, cpu.PC);
    }

    [Fact]
    public void BrkGoesThroughTheIrqVector()
    {
        var (cpu, ram) = Machine("00 EA");
        ram.Vector(0xFFFE, 0x0900);
        cpu.StepNative();
        Assert.Equal(0x0900, cpu.PC);
        Assert.Equal(CpuStop.None, cpu.StopReason);
        Assert.Equal(0x0602, ram.Data[0x1FD] << 8 | ram.Data[0x1FC]);
        Assert.NotEqual(0, ram.Data[0x1FB] & Cpu6502.FlagB);
    }

    [Fact]
    public void SetOverflowRaisesTheVFlag()
    {
        var (cpu, _) = Machine("50 FE B8");                    // BVC * ; CLV
        cpu.StepNative(); cpu.StepNative();
        Assert.Equal(0x0600, cpu.PC);                          // waiting
        cpu.SetOverflow();
        cpu.StepNative();
        Assert.Equal(0x0602, cpu.PC);
        cpu.StepNative();
        Assert.False(cpu.GetFlag(Cpu6502.FlagV));
    }

    [Fact]
    public void TheDataAccessIsTheLastCycleOfTheInstruction()
    {
        var (cpu, ram) = Machine("AD 00 03 BD 80 03");         // LDA $0300 (4 cycles), LDA $0380,X (5 with a page crossing)
        long start = cpu.Cycles;
        ram.AccessCycles.Clear();
        cpu.StepNative();
        Assert.Equal(start + 3, ram.AccessCycles[^1]);         // cycles 0-2 fetch the opcode and operand, the fourth reads the data
        Assert.Equal(start + 4, cpu.Cycles);
        cpu.X = 0xFF;
        cpu.StepNative();
        Assert.Equal(start + 8, ram.AccessCycles[^1]);
        Assert.Equal(start + 9, cpu.Cycles);
    }

    [Fact]
    public void ReadModifyWriteAccessesSpreadOverTheLastThreeCycles()
    {
        var (cpu, ram) = Machine("EE 00 03");                  // INC $0300 (6 cycles)
        long start = cpu.Cycles;
        ram.AccessCycles.Clear();
        cpu.StepNative();
        // the read, the write of the old value and the write of the new one
        Assert.Equal(new[] { start + 3, start + 4, start + 5 }, ram.AccessCycles.Skip(ram.AccessCycles.Count - 3).ToArray());
    }

    [Fact]
    public void AnUnimplementedOpcodeStopsTheStep()
    {
        var (cpu, _) = Machine("02");
        cpu.StepNative();
        Assert.Equal(CpuStop.IllegalOpcode, cpu.StopReason);
        Assert.Equal(0x0600, cpu.PC);
    }

    [Fact]
    public void TheUnstableImmediateOpcodesUseTheMagicConstant()
    {
        var (cpu, _) = Machine("AB FF");                       // LAX #$FF
        cpu.A = 0x00;
        long start = cpu.Cycles;
        cpu.StepNative();
        Assert.Equal(0xEE, cpu.A);                             // (A | $EE) & $FF
        Assert.Equal(0xEE, cpu.X);
        Assert.True(cpu.GetFlag(Cpu6502.FlagN));
        Assert.Equal(2, cpu.Cycles - start);

        (cpu, _) = Machine("8B FF");                           // XAA #$FF
        cpu.A = 0x11; cpu.X = 0xF3;
        cpu.StepNative();
        Assert.Equal(0xF3, cpu.A);                             // (A | $EE) & X & $FF
    }

    [Fact]
    public void TheStoresThatAndWithTheHighByteTakeFiveCyclesAndMaskTheValue()
    {
        var (cpu, ram) = Machine("9C 00 12");                  // SHY $1200,X
        cpu.Y = 0xFF; cpu.X = 0;
        long start = cpu.Cycles;
        cpu.StepNative();
        Assert.Equal(0x13, ram.Data[0x1200]);                  // Y & (high byte + 1)
        Assert.Equal(5, cpu.Cycles - start);

        (cpu, ram) = Machine("9E 00 12");                      // SHX $1200,Y
        cpu.X = 0xFF; cpu.Y = 0;
        cpu.StepNative();
        Assert.Equal(0x13, ram.Data[0x1200]);

        (cpu, ram) = Machine("9F 00 12");                      // AHX $1200,Y
        cpu.A = 0xFF; cpu.X = 0x0F; cpu.Y = 0;
        cpu.StepNative();
        Assert.Equal(0x03, ram.Data[0x1200]);                  // A & X & $13
    }

    [Fact]
    public void WhenTheIndexCrossesAPageTheMaskedValueBecomesTheHighByteOfTheAddress()
    {
        var (cpu, ram) = Machine("9E F0 12");                  // SHX $12F0,Y with Y = $20: crosses into $13xx
        cpu.X = 0x07; cpu.Y = 0x20;
        cpu.StepNative();
        Assert.Equal(0x03, ram.Data[0x0310]);                  // X & $13 = 3, so the address is $0310, not $1310
        Assert.Equal(0, ram.Data[0x1310]);
    }

    [Fact]
    public void TasAndLasWorkOnTheStackPointer()
    {
        var (cpu, ram) = Machine("9B 00 12");                  // TAS $1200,Y
        cpu.A = 0xF0; cpu.X = 0x3C; cpu.Y = 0;
        cpu.StepNative();
        Assert.Equal(0x30, cpu.SP);                            // A & X
        Assert.Equal(0x10, ram.Data[0x1200]);                  // SP & $13

        (cpu, ram) = Machine("BB 00 03");                      // LAS $0300,Y
        ram.Data[0x300] = 0xF0;
        cpu.SP = 0x3C; cpu.Y = 0;
        cpu.StepNative();
        Assert.Equal(0x30, cpu.A);
        Assert.Equal(0x30, cpu.X);
        Assert.Equal(0x30, cpu.SP);
    }

    [Fact]
    public void AHxIndirectIndexedStoresThroughThePointer()
    {
        var (cpu, ram) = Machine("93 40");                     // AHX ($40),Y
        ram.Data[0x40] = 0x00; ram.Data[0x41] = 0x12;
        cpu.A = 0xFF; cpu.X = 0x0F; cpu.Y = 0x05;
        long start = cpu.Cycles;
        cpu.StepNative();
        Assert.Equal(0x03, ram.Data[0x1205]);
        Assert.Equal(6, cpu.Cycles - start);
    }

    [Fact]
    public void TheInterpretersProcessorStillRefusesTheUnstableOpcodes()
    {
        var bus = new Bus();
        var cpu = new Cpu6502(bus);
        bus.Ram[0x200] = 0xAB; bus.Ram[0x201] = 0xFF;
        Assert.Equal(CpuStop.IllegalOpcode, cpu.Call(0x200));
    }

    [Fact]
    public void CallIsNotForNativeProcessors() =>
        Assert.Throws<InvalidOperationException>(() => new Cpu6502(new Ram()).Call(0x600));

    // ---------- lockstep ----------
    sealed class Member : ILockstepMember
    {
        public long Cycles { get; private set; }
        public double Hz { get; }
        public int Steps { get; private set; }
        readonly int _length;
        readonly List<string> _log;
        readonly string _name;

        public Member(string name, double hz, int length, List<string> log) { _name = name; Hz = hz; _length = length; _log = log; }

        public void Step() { Cycles += _length; Steps++; _log.Add(_name); }
    }

    [Fact]
    public void TheMemberFurthestBehindStepsNext()
    {
        var log = new List<string>();
        var a = new Member("a", 1000, 2, log);     // 2 ms per step
        var b = new Member("b", 1000, 3, log);     // 3 ms per step
        new Lockstep(a, b).Run(7);
        // (a,b) = (0,0) a | (2,0) b | (2,3) a | (4,3) b | (4,6) a | (6,6) a | (8,6) b
        Assert.Equal(new[] { "a", "b", "a", "b", "a", "a", "b" }, log);
    }

    [Fact]
    public void ClocksOfDifferentSpeedKeepTheirRatio()
    {
        var log = new List<string>();
        var c64 = new Member("c64", 985248, 3, log);
        var drive = new Member("1541", 1000000, 3, log);
        new Lockstep(c64, drive).RunUntil(1.0);
        Assert.InRange(c64.Cycles, 985248, 985248 + 3);
        Assert.InRange(drive.Cycles, 1000000, 1000000 + 3);
    }

    [Fact]
    public void HaltStopsTheRun()
    {
        var a = new Member("a", 1000, 2, new List<string>());
        new Lockstep(a).RunUntil(100, () => a.Steps == 5);
        Assert.Equal(5, a.Steps);
    }

    [Fact]
    public void TwoProcessorsTalkThroughSharedMemoryInStep()
    {
        // A counts up in a mailbox byte; B copies it to its own memory. B only ever lags A by a step or two.
        var mailbox = new byte[1];
        var (a, _) = Machine("EE 00 D0 4C 00 06", mailbox: mailbox);                       // INC $D000 : JMP $0600
        var (b, bRam) = Machine("AD 00 D0 8D 00 03 4C 00 06", mailbox: mailbox);           // LDA $D000 : STA $0300 : JMP $0600
        new Lockstep(new CpuMember(a, 985248), new CpuMember(b, 1000000)).RunUntil(0.01);
        Assert.InRange(a.Cycles, 9852 - 10, 9852 + 10);
        Assert.InRange(b.Cycles, 10000 - 10, 10000 + 10);
        Assert.InRange((mailbox[0] - bRam.Data[0x300]) & 0xFF, 0, 2);
        Assert.True(mailbox[0] != 0);
    }
}
