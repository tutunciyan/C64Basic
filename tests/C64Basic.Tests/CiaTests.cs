using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public sealed class TestInput : IInputDevice
{
    readonly byte[] _columns = new byte[8];
    readonly byte[] _joy = new byte[3];

    public byte KeyColumn(int column) => _columns[column];
    public byte Joystick(int port) => _joy[port];

    /// <summary>Holds key number <c>column * 8 + row</c>.</summary>
    public TestInput Press(int key) { _columns[key / 8] |= (byte)(1 << key % 8); return this; }
    public TestInput Joy(int port, byte bits) { _joy[port] = bits; return this; }
}

public class CiaTests
{
    const double Cycle = 1.0 / Cia.ClockHz;

    /// <summary>A bus on a clock the test moves by hand.</summary>
    sealed class Clocked
    {
        public double Now;
        public Bus Bus = new();
        public Clocked() => Bus.Seconds = () => Now;
        public void Wait(double cycles) => Now += cycles * Cycle;
        public int Read(int a) => Bus.Read(a);
        public void Write(int a, int v) => Bus.Write(a, (byte)v);
    }

    static (Interpreter Interp, TestConsole Console) Session()
    {
        var console = new TestConsole();
        return (new Interpreter(console, new MemoryFileSystem()), console);
    }

    [Fact]
    public void KernalDefaultsMatchAfterReset()
    {
        var bus = new Bus();
        Assert.Equal(0x81, bus.Read(0xDC0E));
        Assert.Equal(0xFF, bus.Read(0xDC02));
        Assert.Equal(0x3F, bus.Read(0xDD02));
        Assert.Equal(0x17, bus.Read(0xDD00) & 0x3F);
    }

    [Fact]
    public void RegistersMirrorEvery16Bytes()
    {
        var c = new Clocked();
        c.Write(0xDC02 + 16, 0x0F);
        Assert.Equal(0x0F, c.Read(0xDC02));
        Assert.Equal(0x0F, c.Read(0xDC02 + 0xF0));
    }

    [Fact]
    public void TimerCountsDownFromItsLatch()
    {
        var c = new Clocked();
        c.Write(0xDC0E, 0);                  // stop
        c.Write(0xDC04, 0x00);
        c.Write(0xDC05, 0x10);               // latch 4096, loaded because stopped
        c.Write(0xDC0E, 0x01);               // start
        c.Wait(1000);
        Assert.Equal(4096 - 1000, c.Read(0xDC04) | c.Read(0xDC05) << 8);
    }

    [Fact]
    public void TimerReloadsAndSetsInterruptFlag()
    {
        var c = new Clocked();
        c.Write(0xDC0D, 0x7F);               // no interrupt enabled, so bit 7 stays clear
        c.Write(0xDC0E, 0);
        c.Write(0xDC04, 99);
        c.Write(0xDC05, 0);                  // period 100
        c.Write(0xDC0E, 0x01);
        c.Wait(250);
        Assert.Equal(49, c.Read(0xDC04));     // two reloads and 50 cycles into the third period
        Assert.Equal(0x01, c.Read(0xDC0D));   // TA underflow
        Assert.Equal(0, c.Read(0xDC0D));      // reading acknowledged it
    }

    [Fact]
    public void OneShotStopsAfterUnderflow()
    {
        var c = new Clocked();
        c.Write(0xDC0E, 0);
        c.Write(0xDC04, 9);
        c.Write(0xDC05, 0);
        c.Write(0xDC0E, 0x09);               // start, one-shot
        c.Wait(100);
        Assert.Equal(0, c.Read(0xDC0E) & 1);
        Assert.Equal(9, c.Read(0xDC04));
    }

    [Fact]
    public void InterruptMaskControlsTheIrqLine()
    {
        var c = new Clocked();
        c.Write(0xDC0D, 0x7F);               // mask everything
        c.Write(0xDC0E, 0);
        c.Write(0xDC04, 9);
        c.Write(0xDC05, 0);
        c.Write(0xDC0E, 0x01);
        c.Wait(100);
        Assert.False(c.Bus.Cia1.InterruptPending);
        c.Write(0xDC0D, 0x81);               // enable timer A
        Assert.True(c.Bus.Cia1.InterruptPending);
        Assert.Equal(0x81, c.Read(0xDC0D));  // flag plus bit 7
        Assert.False(c.Bus.Cia1.InterruptPending);
    }

    [Fact]
    public void DefaultTimerIsTheSixtyHertzSystemInterrupt()
    {
        var c = new Clocked();
        Assert.False(c.Bus.Cia1.InterruptPending);
        c.Now = 1.0 / 60 + 0.0005;
        Assert.True(c.Bus.Cia1.InterruptPending);
    }

    [Fact]
    public void TimerBCountsTimerAUnderflows()
    {
        var c = new Clocked();
        c.Write(0xDC0E, 0);
        c.Write(0xDC0F, 0);
        c.Write(0xDC04, 9); c.Write(0xDC05, 0);   // A: period 10
        c.Write(0xDC06, 99); c.Write(0xDC07, 0);  // B: 99 + 1
        c.Write(0xDC0F, 0x41);                    // B counts A underflows, started
        c.Write(0xDC0E, 0x01);
        c.Wait(10 * 30);
        Assert.Equal(99 - 30, c.Read(0xDC06));
    }

    [Fact]
    public void ForceLoadResetsTheCounter()
    {
        var c = new Clocked();
        c.Write(0xDC0E, 0);
        c.Write(0xDC04, 0x34); c.Write(0xDC05, 0x12);
        c.Write(0xDC04, 0x78);                    // latch only; a stopped timer keeps its counter
        c.Write(0xDC0E, 0x10);                    // force load
        Assert.Equal(0x1278, c.Read(0xDC04) | c.Read(0xDC05) << 8);
        Assert.Equal(0, c.Read(0xDC0E) & 0x10);   // strobe bit reads back 0
    }

    [Fact]
    public void KeyboardMatrixIsScannedThroughThePorts()
    {
        var bus = new Bus { Input = new TestInput().Press(1 * 8 + 4) }; // column 1, row 4
        bus.Write(0xDC02, 0xFF);
        bus.Write(0xDC03, 0x00);
        bus.Write(0xDC00, 0xFF);
        Assert.Equal(0xFF, bus.Read(0xDC01));     // no column selected
        bus.Write(0xDC00, 0xFD);                  // select column 1
        Assert.Equal(0xEF, bus.Read(0xDC01));     // row 4 pulled low
        bus.Write(0xDC00, 0xFE);                  // column 0
        Assert.Equal(0xFF, bus.Read(0xDC01));
        bus.Write(0xDC00, 0x00);                  // all columns
        Assert.Equal(0xEF, bus.Read(0xDC01));
    }

    [Fact]
    public void KeyboardIsScannableByRowToo()
    {
        var bus = new Bus { Input = new TestInput().Press(3 * 8 + 6) };
        bus.Write(0xDC02, 0x00);
        bus.Write(0xDC03, 0xFF);
        bus.Write(0xDC01, 0xBF);                  // select row 6
        Assert.Equal(0xF7, bus.Read(0xDC00));     // column 3 pulled low
    }

    [Fact]
    public void JoysticksReadActiveLow()
    {
        var bus = new Bus { Input = new TestInput().Joy(2, 0b10001).Joy(1, 0b00100) };
        bus.Write(0xDC02, 0x00);                  // read port A as inputs
        bus.Write(0xDC03, 0x00);
        Assert.Equal(0b1110_1110, bus.Read(0xDC00));   // up and fire on port 2
        Assert.Equal(0b1111_1011, bus.Read(0xDC01));   // left on port 1
    }

    [Fact]
    public void NothingPressedWithoutAnInputDevice()
    {
        var bus = new Bus();
        bus.Write(0xDC02, 0x00);
        bus.Write(0xDC03, 0x00);
        Assert.Equal(0xFF, bus.Read(0xDC00));
        Assert.Equal(0xFF, bus.Read(0xDC01));
    }

    [Fact]
    public void Peek197GivesKernalKeyIndex()
    {
        Assert.Equal(64, new Bus { Input = new TestInput() }.Read(197));
        Assert.Equal(1, new Bus { Input = new TestInput().Press(1) }.Read(197));    // RETURN
        Assert.Equal(60, new Bus { Input = new TestInput().Press(60) }.Read(197));  // SPACE
    }

    [Fact]
    public void ShiftKeysAreReportedInFlagsNotAsKeys()
    {
        var bus = new Bus { Input = new TestInput().Press(15).Press(58).Press(10) };  // L-shift, CTRL, A
        Assert.Equal(10, bus.Read(197));
        Assert.Equal(1 | 4, bus.Read(653));
        bus = new Bus { Input = new TestInput().Press(61).Press(52) };                // C=, R-shift
        Assert.Equal(64, bus.Read(197));
        Assert.Equal(1 | 2, bus.Read(653));
    }

    [Fact]
    public void Peek197IsPlainRamWithoutInput() =>
        Assert.Equal(" 5 \n", Basic.Run("POKE 197,5:PRINT PEEK(197)"));

    [Fact]
    public void BasicReadsTheKeyboardWithPeek()
    {
        var (interp, console) = Session();
        interp.Bus.Input = new TestInput().Press(9);                  // W
        interp.ProcessLine("PRINT PEEK(197)");
        Assert.Equal(" 9 \n", console.Output);
    }

    [Fact]
    public void BasicJoystickReadLikeTheClassicIdiom()
    {
        var (interp, console) = Session();
        interp.Bus.Input = new TestInput().Joy(2, 0b10010);           // fire + down
        interp.ProcessLine("J=PEEK(56320) AND 31:PRINT 31-J");
        Assert.Equal(" 18 \n", console.Output);
    }

    [Fact]
    public void TimeOfDayRunsAndIsBcd()
    {
        var c = new Clocked();
        c.Write(0xDC0F, 0x00);
        c.Write(0xDC0B, 0x11);  // 11 am (BCD, bit 7 = PM)
        c.Write(0xDC0A, 0x59);
        c.Write(0xDC09, 0x58);
        c.Write(0xDC08, 0x00);  // writing tenths starts it
        c.Now += 3.25;
        Assert.Equal(0x92, c.Read(0xDC0B));  // 12:00:01.2 -> 12 pm
        Assert.Equal(0x00, c.Read(0xDC0A));
        Assert.Equal(0x01, c.Read(0xDC09));
        Assert.Equal(0x02, c.Read(0xDC08));
    }

    [Fact]
    public void ReadingHoursLatchesUntilTenths()
    {
        var c = new Clocked();
        c.Write(0xDC0B, 0x01); c.Write(0xDC0A, 0x00); c.Write(0xDC09, 0x09); c.Write(0xDC08, 0);
        Assert.Equal(0x01, c.Read(0xDC0B));
        c.Now += 2;
        Assert.Equal(0x09, c.Read(0xDC09));   // frozen
        Assert.Equal(0x00, c.Read(0xDC08));   // releases the latch
        Assert.Equal(0x11, c.Read(0xDC09));   // 9 + 2 seconds -> 11 (BCD)
    }

    [Fact]
    public void WritingHoursHaltsTheClockUntilTenths()
    {
        var c = new Clocked();
        c.Write(0xDC0B, 0x02);
        c.Now += 5;
        Assert.Equal(0x00, c.Read(0xDC09));
        c.Write(0xDC08, 0);
        c.Now += 5;
        Assert.Equal(0x05, c.Read(0xDC09));
    }

    [Fact]
    public void JiffyClockAtZeroPageFollowsTi()
    {
        var (interp, console) = Session();
        interp.Bus.Seconds = () => 10;
        interp.ProcessLine("PRINT TI;PEEK(160)*65536+PEEK(161)*256+PEEK(162)");
        Assert.Equal(" 600  600 \n", console.Output);
    }

    [Fact]
    public void PokingTheJiffyClockSetsTi()
    {
        var (interp, console) = Session();
        interp.Bus.Seconds = () => 100;
        interp.ProcessLine("POKE 160,0:POKE 161,1:POKE 162,44:PRINT TI");
        Assert.Equal(" 300 \n", console.Output);
    }

    [Fact]
    public void VicBankFollowsCia2()
    {
        var bus = new Bus();
        bus.Write(0xDD00, 0x14);                   // bits 1-0 = 00 -> bank 3
        bus.Ram[0xC000 + 1024 + 1016] = 5;
        for (int i = 0; i < 63; i++) bus.Ram[0xC000 + 5 * 64 + i] = 0xFF;
        bus.Write(0xD000, 24); bus.Write(0xD001, 50); bus.Write(0xD015, 1);
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        bus.Vic.Render(frame);
        Assert.Equal(Vic2.Palette[1], frame[36 * Vic2.FrameWidth + 32]);
    }
}
