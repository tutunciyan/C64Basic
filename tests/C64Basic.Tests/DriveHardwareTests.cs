using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>The 1541's own computer: memory map, ROM and the two VIAs, run with small hand-assembled firmware.</summary>
public class DriveHardwareTests
{
    /// <summary>A ROM image with code at $C000 (and any extra pieces), the reset vector pointing there and the IRQ vector at <paramref name="irq"/>.</summary>
    static byte[] Rom(string code, int irq = 0xC100, params (int Address, string Code)[] extra)
    {
        var rom = new byte[Drive1541.RomSize];
        Array.Fill(rom, (byte)0xEA);
        void Put(int address, string hex) => Convert.FromHexString(hex.Replace(" ", "")).CopyTo(rom, address & 0x3FFF);
        Put(0xC000, code);
        foreach (var (address, c) in extra) Put(address, c);
        Put(0xFFFC, "00 C0");
        Put(0xFFFE, $"{irq & 0xFF:X2} {irq >> 8:X2}");
        Put(0xFFFA, "00 C0");
        return rom;
    }

    static Drive1541 Drive(string code, int irq = 0xC100, params (int Address, string Code)[] extra)
    {
        var drive = new Drive1541(Rom(code, irq, extra));
        drive.Reset();
        return drive;
    }

    static void RunCycles(Drive1541 drive, long cycles)
    {
        long end = drive.Cycles + cycles;
        while (drive.Cycles < end) drive.Step();
    }

    // ---------- memory map ----------
    [Fact]
    public void ResetStartsAtTheVectorInTheRom()
    {
        var drive = Drive("A2 05 4C 02 C0");     // LDX #5 ; JMP $C002
        Assert.Equal(0xC000, drive.Cpu.PC);
        drive.Step(); drive.Step();
        Assert.Equal(5, drive.Cpu.X);
        Assert.Equal(0xC002, drive.Cpu.PC);
    }

    [Fact]
    public void RamIs2KAtTheBottomOfMemoryAndMirrorsAtTheNext8K()
    {
        // LDA #$5A ; STA $07FF ; LDA #$A5 ; STA $2000 ; LDA $0000 ; LDX $27FF
        var drive = Drive("A9 5A 8D FF 07 A9 A5 8D 00 20 AD 00 00 AE FF 27");
        for (int i = 0; i < 6; i++) drive.Step();
        Assert.Equal(0x5A, drive.Ram[0x7FF]);
        Assert.Equal(0xA5, drive.Ram[0]);          // $2000 is the same RAM as $0000
        Assert.Equal(0xA5, drive.Cpu.A);
        Assert.Equal(0x5A, drive.Cpu.X);           // and $27FF the same as $07FF
    }

    [Fact]
    public void AddressesThatDecodeToNothingReadTheHighByteOfTheAddress()
    {
        var drive = Drive("AD 34 0A AE 34 12");   // LDA $0A34 ; LDX $1234
        drive.Step(); drive.Step();
        Assert.Equal(0x0A, drive.Cpu.A);
        Assert.Equal(0x12, drive.Cpu.X);
    }

    [Fact]
    public void RomIsAtC000AndMirroredAt8000AndCannotBeWritten()
    {
        // LDA #$11 ; STA $C000 ; LDA $8000 ; LDX $C000
        var drive = Drive("A9 11 8D 00 C0 AD 00 80 AE 00 C0");
        for (int i = 0; i < 4; i++) drive.Step();
        Assert.Equal(0xA9, drive.Cpu.A);           // the first byte of the program: LDA #
        Assert.Equal(0xA9, drive.Cpu.X);
    }

    [Fact]
    public void TheViasAreAt1800And1C00AndRepeatEverySixteenBytes()
    {
        // LDA #$0F ; STA $1802 (DDRB of VIA 1) ; LDA #$F0 ; STA $1C02 (DDRB of VIA 2) ; LDX $1812 ; LDY $1C22
        var drive = Drive("A9 0F 8D 02 18 A9 F0 8D 02 1C AE 12 18 AC 22 1C");
        for (int i = 0; i < 6; i++) drive.Step();
        Assert.Equal(0x0F, drive.Via1.Ddrb);
        Assert.Equal(0xF0, drive.Via2.Ddrb);
        Assert.Equal(0x0F, drive.Cpu.X);
        Assert.Equal(0xF0, drive.Cpu.Y);
    }

    [Fact]
    public void ARomOfTheWrongSizeIsRefused() =>
        Assert.Throws<ArgumentException>(() => new Drive1541(new byte[8192]));

    // ---------- firmware driven by the timers ----------
    [Fact]
    public void ATimerInterruptRunsTheHandlerOncePerPeriod()
    {
        // SEI ; ACR=$40 (T1 free-running) ; IER=$C0 ; latch $03E7 (999) ; CLI ; JMP *
        // handler: LDA $1C04 (clears the flag) ; INC $10 ; RTI
        var drive = Drive("78 A9 40 8D 0B 1C A9 C0 8D 0E 1C A9 E7 8D 04 1C A9 03 8D 05 1C 58 4C 16 C0",
            0xC100, (0xC100, "AD 04 1C E6 10 40"));
        RunCycles(drive, 100_000);
        // one interrupt every 1001 cycles plus the handler's 7 + 4 + 5 + 6 cycles of overhead stay inside the period
        Assert.InRange(drive.Ram[0x10], 98, 100);
        Assert.False(drive.Cpu.GetFlag(Cpu6502.FlagI));
    }

    [Fact]
    public void AnInterruptThatIsNotEnabledDoesNotReachTheProcessor()
    {
        // free-running timer but IER stays clear: the handler must never run
        var drive = Drive("58 A9 40 8D 0B 1C A9 10 8D 04 1C A9 00 8D 05 1C 4C 10 C0",
            0xC100, (0xC100, "E6 10 40"));
        RunCycles(drive, 5000);
        Assert.Equal(0, drive.Ram[0x10]);
        Assert.NotEqual(0, drive.Via2.Ifr & 0x40);
    }

    [Fact]
    public void BothViasShareTheProcessorsIrqLine()
    {
        // VIA 1 T1 one-shot, interrupt enabled; the handler counts and then disables the source
        var drive = Drive("78 A9 C0 8D 0E 18 A9 20 8D 04 18 A9 00 8D 05 18 58 4C 11 C0",
            0xC100, (0xC100, "E6 10 A9 40 8D 0E 18 40"));
        RunCycles(drive, 2000);
        Assert.Equal(1, drive.Ram[0x10]);
    }

    // ---------- the 6522 on its own ----------
    sealed class Rig
    {
        public long Now;
        public byte A = 0xFF, B = 0xFF;
        public readonly Via6522 Via;

        public Rig()
        {
            Via = new Via6522 { Clock = () => Now, PinsA = () => A, PinsB = () => B };
            Via.Reset();
        }

        public int Read(int reg) => Via.Read(reg);
        public void Write(int reg, int value) => Via.Write(reg, (byte)value);
    }

    [Fact]
    public void Timer1OneShotSetsItsFlagAfterNPlusTwoCycles()
    {
        var r = new Rig();
        r.Write(11, 0x00);          // one-shot
        r.Write(6, 10);             // latch low
        r.Write(5, 0);              // start: 10
        r.Now += 11;
        Assert.Equal(0, r.Read(13) & 0x40);
        r.Now += 1;
        Assert.NotEqual(0, r.Read(13) & 0x40);
        Assert.Equal(0, r.Read(13) & 0x80);          // not enabled: no interrupt
        r.Write(14, 0xC0);
        Assert.True(r.Via.IrqActive);
        Assert.Equal(0xC0, r.Read(13) & 0xC0);
        r.Read(4);                                    // reading the low counter clears it
        Assert.False(r.Via.IrqActive);
    }

    [Fact]
    public void Timer1CountsDownAndFreeRunsWithAPeriodOfNPlusTwo()
    {
        var r = new Rig();
        r.Write(11, 0x40);
        r.Write(4, 0x63);           // 99
        r.Write(5, 0);
        r.Now += 5;
        Assert.Equal(94, r.Read(4));
        r.Now += 95;                // 100 cycles after the start: the latch is 99, the period 101
        Assert.Equal(0xFF, r.Read(4));              // -1, the cycle before it reloads
        Assert.Equal(0, r.Via.Ifr & 0x40);
        r.Now += 1;
        Assert.NotEqual(0, r.Via.Ifr & 0x40);
        Assert.Equal(99, r.Read(4));                // reloaded (and the read clears the flag)
        Assert.Equal(0, r.Via.Ifr & 0x40);
        r.Now += 101 * 3;
        Assert.NotEqual(0, r.Via.Ifr & 0x40);
    }

    [Fact]
    public void WritingTheHighCounterClearsTheFlagAndRestartsTheTimer()
    {
        var r = new Rig();
        r.Write(11, 0x00);
        r.Write(6, 4);
        r.Write(5, 0);
        r.Now += 10;
        Assert.NotEqual(0, r.Via.Ifr & 0x40);
        r.Write(5, 0);
        Assert.Equal(0, r.Via.Ifr & 0x40);
        r.Now += 3;
        Assert.Equal(0, r.Via.Ifr & 0x40);
        r.Now += 3;
        Assert.NotEqual(0, r.Via.Ifr & 0x40);
    }

    [Fact]
    public void Timer2IsAOneShot()
    {
        var r = new Rig();
        r.Write(8, 20);
        r.Write(9, 0);
        r.Now += 21;
        Assert.Equal(0, r.Via.Ifr & 0x20);
        r.Now += 1;
        Assert.NotEqual(0, r.Via.Ifr & 0x20);
        r.Read(8);
        Assert.Equal(0, r.Via.Ifr & 0x20);
        r.Now += 1000;
        Assert.Equal(0, r.Via.Ifr & 0x20);           // it does not fire again
    }

    [Fact]
    public void InterruptEnableRegisterSetsAndClearsBits()
    {
        var r = new Rig();
        r.Write(14, 0xFF);
        Assert.Equal(0xFF, r.Read(14));
        r.Write(14, 0x05);                           // bit 7 clear: these bits are cleared
        Assert.Equal(0xFA, r.Read(14));
    }

    [Fact]
    public void WritingOnesToTheFlagRegisterClearsFlags()
    {
        var r = new Rig();
        r.Via.SetCa1(true); r.Via.SetCa1(false);     // PCR 0: negative edge
        Assert.Equal(IrqCa1, r.Read(13) & 0x7F);
        r.Write(13, 0x02);
        Assert.Equal(0, r.Read(13) & 0x7F);
    }

    const int IrqCa1 = 2;

    [Fact]
    public void Ca1EdgeFollowsPcrAndReadingPortAClearsTheFlag()
    {
        var r = new Rig();
        r.Via.SetCa1(true);                           // rising edge with PCR bit 0 = 0: ignored
        Assert.Equal(0, r.Read(13) & IrqCa1);
        r.Via.SetCa1(false);
        Assert.NotEqual(0, r.Read(13) & IrqCa1);
        r.Read(1);
        Assert.Equal(0, r.Read(13) & IrqCa1);
        r.Write(12, 0x01);                            // positive edge
        r.Via.SetCa1(true);
        Assert.NotEqual(0, r.Read(13) & IrqCa1);
        r.Read(15);                                   // the no-handshake address leaves the flag alone
        Assert.NotEqual(0, r.Read(13) & IrqCa1);
    }

    [Fact]
    public void PortALatchingKeepsTheValueFromTheEdge()
    {
        var r = new Rig();
        r.Write(11, 0x01);                            // latch port A on CA1
        r.A = 0x42;
        r.Via.SetCa1(true); r.Via.SetCa1(false);
        r.A = 0x99;                                   // the pins move on, the latch does not
        Assert.Equal(0x42, r.Read(1));
        r.Write(11, 0x00);
        Assert.Equal(0x99, r.Read(1));
    }

    [Fact]
    public void PortsMixOutputLatchAndInputPins()
    {
        var r = new Rig();
        r.A = 0b1010_1010;
        r.Write(3, 0x0F);                             // low nibble output
        r.Write(1, 0x05);
        Assert.Equal(0b1010_0101, r.Read(1));
        Assert.Equal(0b1111_0101, r.Via.PortAOutput);   // inputs are not driven low by the chip
        r.B = 0x3C;
        r.Write(2, 0xF0);
        r.Write(0, 0xA0);
        Assert.Equal(0xA0 | 0x0C, r.Read(0));
    }

    [Fact]
    public void ChangingAnOutputRaisesTheChangeEvent()
    {
        var r = new Rig();
        int b = 0, a = 0;
        r.Via.PortBChanged += () => b++;
        r.Via.PortAChanged += () => a++;
        r.Write(2, 0xFF); r.Write(0, 1);
        r.Write(1, 2);
        Assert.Equal(2, b);
        Assert.Equal(1, a);
    }

    [Fact]
    public void Ca2AndCb2ManualModesDriveTheirPins()
    {
        var r = new Rig();
        r.Write(12, 0xEE);                            // CB2 manual high, CA2 manual high
        Assert.True(r.Via.Ca2Output); Assert.True(r.Via.Cb2Output);
        r.Write(12, 0xCC);                            // both manual low
        Assert.False(r.Via.Ca2Output); Assert.False(r.Via.Cb2Output);
    }

    [Fact]
    public void Timer1DrivesPb7WhenAcrBit7IsSet()
    {
        var r = new Rig();
        r.Write(2, 0x80);
        r.Write(11, 0xC0);                            // free-run with PB7 output
        r.Write(4, 9);
        r.Write(5, 0);
        Assert.Equal(0, r.Via.PortBOutput & 0x80);    // one-shot style start: low
        r.Now += 11;
        Assert.NotEqual(0, r.Via.PortBOutput & 0x80); // toggled by the first underflow
        r.Now += 11;
        Assert.Equal(0, r.Via.PortBOutput & 0x80);
    }
}
