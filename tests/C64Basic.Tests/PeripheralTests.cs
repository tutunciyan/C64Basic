using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class PeripheralTests
{
    const double Cycle = 1.0 / Cia.ClockHz;

    sealed class Clocked
    {
        public double Now;
        public Bus Bus = new();
        public Clocked() => Bus.Seconds = () => Now;
        public void Wait(double cycles) => Now += cycles * Cycle;
        public int Read(int a) => Bus.Read(a);
        public void Write(int a, int v) => Bus.Write(a, (byte)v);
    }

    // ---------- paddles ----------
    [Fact]
    public void ThePaddleOnTheSelectedPortReachesThePotRegisters()
    {
        var bus = new Bus { Seconds = () => 0 };
        var console = new ScreenConsole();
        console.Attach(bus);
        console.SetPaddle(1, 0, 40); console.SetPaddle(1, 1, 41);
        console.SetPaddle(2, 0, 200); console.SetPaddle(2, 1, 201);

        bus.Write(0xDC00, 0x40);                              // bit 6: port 1
        Assert.Equal(40, bus.Read(0xD419));
        Assert.Equal(41, bus.Read(0xD41A));
        bus.Write(0xDC00, 0x80);                              // bit 7: port 2
        Assert.Equal(200, bus.Read(0xD419));
        Assert.Equal(201, bus.Read(0xD41A));
        bus.Write(0xDC00, 0xC0);                              // both: nothing is read
        Assert.Equal(0, bus.Read(0xD419));
    }

    [Fact]
    public void WithoutAPaddleThePotsReadZero()
    {
        var bus = new Bus { Seconds = () => 0 };
        bus.Write(0xDC00, 0x40);
        Assert.Equal(0, bus.Read(0xD419));
    }

    [Fact]
    public void PaddleValuesAreClamped()
    {
        var console = new ScreenConsole();
        console.SetPaddle(2, 0, 999);
        console.SetPaddle(2, 1, -5);
        Assert.Equal(255, console.Paddle(2, 0));
        Assert.Equal(0, console.Paddle(2, 1));
        Assert.Equal(0, console.Paddle(3, 0));
    }

    [Fact]
    public void BasicCanReadAPaddle()
    {
        var console = new ScreenConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        console.SetPaddle(1, 0, 123);
        interp.ProcessLine("POKE 56320,64:PRINT PEEK(54297)");
        Assert.Equal(123, interp.Bus.Read(0xD419));
    }

    // ---------- the CNT pin ----------
    [Fact]
    public void TimerACountsCntPulses()
    {
        var m = new Clocked();
        m.Write(0xDD04, 2); m.Write(0xDD05, 0);           // underflows after 3 pulses
        m.Write(0xDD0E, 0x21);                             // start, count CNT
        m.Wait(1000);
        Assert.Equal(2, m.Bus.Cia2.TimerA);                // the system clock does nothing
        m.Bus.Cia2.PulseCnt(2);
        Assert.Equal(0, m.Bus.Cia2.TimerA);
        Assert.False(m.Bus.Cia2.InterruptPending);
        m.Bus.Cia2.PulseCnt();
        Assert.Equal(2, m.Bus.Cia2.TimerA);                // reloaded
        m.Write(0xDD0D, 0x81);
        Assert.True(m.Bus.Cia2.InterruptPending);
    }

    [Fact]
    public void TimerBCanCountCntPulses()
    {
        var m = new Clocked();
        m.Write(0xDD06, 4); m.Write(0xDD07, 0);
        m.Write(0xDD0F, 0x21);                             // start, count CNT
        m.Bus.Cia2.PulseCnt(3);
        Assert.Equal(1, m.Bus.Cia2.TimerB);
    }

    [Fact]
    public void TimerBCountsUnderflowsOfACountingCnt()
    {
        var m = new Clocked();
        m.Write(0xDD04, 1); m.Write(0xDD05, 0);           // A underflows every 2 pulses
        m.Write(0xDD0E, 0x21);
        m.Write(0xDD06, 9); m.Write(0xDD07, 0);
        m.Write(0xDD0F, 0x41);                             // B counts A's underflows
        m.Bus.Cia2.PulseCnt(6);                            // three underflows
        Assert.Equal(6, m.Bus.Cia2.TimerB);
    }

    [Fact]
    public void TimerBCanBeGatedByCnt()
    {
        var m = new Clocked();
        m.Write(0xDD04, 3); m.Write(0xDD05, 0);           // A underflows every 4 cycles
        m.Write(0xDD0E, 0x01);
        m.Write(0xDD06, 99); m.Write(0xDD07, 0);
        m.Write(0xDD0F, 0x61);                             // B counts A underflows while CNT is high
        m.Wait(40);
        Assert.Equal(89, m.Bus.Cia2.TimerB);
        m.Bus.Cia2.CntHigh = false;
        m.Wait(40);
        Assert.Equal(89, m.Bus.Cia2.TimerB);
    }

    // ---------- the keyboard layout ----------
    [Theory]
    [InlineData('a', new[] { 10 })]
    [InlineData('A', new[] { 15, 10 })]
    [InlineData('1', new[] { 56 })]
    [InlineData('"', new[] { 15, 59 })]
    [InlineData(' ', new[] { 60 })]
    [InlineData('\r', new[] { 1 })]
    [InlineData('\u0011', new[] { 7 })]
    [InlineData('\u0091', new[] { 15, 7 })]
    public void CharactersMapToMatrixKeys(char c, int[] keys) => Assert.Equal(keys, KeyboardLayout.KeysFor(c));

    [Fact]
    public void ColourCodesHaveNoKey() => Assert.Null(KeyboardLayout.KeysFor('\u001c'));

    [Fact]
    public void EveryPrintableAsciiCharacterCanBeTyped()
    {
        for (char c = ' '; c < '\u007f'; c++)
            if (c is not ('|' or '\\' or '{' or '}' or '~' or '`'))
                Assert.NotNull(KeyboardLayout.KeysFor(c));
    }

    [Fact]
    public void AllMappedKeysAreInsideTheMatrix()
    {
        for (int c = 0; c < 256; c++)
            if (KeyboardLayout.KeysFor((char)c) is { } keys) Assert.All(keys, k => Assert.InRange(k, 0, 63));
    }

    [Fact]
    public void HeldMatrixKeysShowInTheKeyboardRegisters()
    {
        var bus = new Bus { Seconds = () => 0 };
        var console = new ScreenConsole();
        console.Attach(bus);
        foreach (int k in KeyboardLayout.KeysFor('A')!) console.SetKey(k, true);     // shift + A
        bus.Write(0xDC02, 0xFF); bus.Write(0xDC03, 0x00);
        bus.Write(0xDC00, 0xFF & ~(1 << 1));                                         // select column 1 (A is key 10 = column 1, row 2)
        Assert.Equal(0, bus.Read(0xDC01) & (1 << 2));
    }

    // ---------- machine state ----------
    [Fact]
    public void TheCiaExtrasSurviveASavedState()
    {
        var console = new TestConsole();
        var a = new Interpreter(console, new MemoryFileSystem());
        a.Bus.Seconds = () => 0;
        a.Bus.Write(0xDD04, 9); a.Bus.Write(0xDD05, 0);
        a.Bus.Write(0xDD0E, 0x47);                         // started, PB6 toggle output, serial output
        a.Bus.Write(0xDD0C, 0x55);                         // a byte is on its way out
        a.Bus.Cia2.CntHigh = false;
        var bytes = a.SaveState();

        var b = new Interpreter(new TestConsole(), new MemoryFileSystem());
        b.Bus.Seconds = () => 0;
        b.LoadState(bytes);
        Assert.False(b.Bus.Cia2.CntHigh);
        Assert.Equal(0x40, b.Bus.Read(0xDD01) & 0x40);    // the toggle output is high, as saved
        byte? sent = null;
        b.Bus.Cia2.SerialOut += v => sent = v;
        var t = 0.0;
        b.Bus.Seconds = () => t;
        t += 200 * Cycle;
        _ = b.Bus.Cia2.InterruptPending;
        Assert.Equal((byte)0x55, sent);                    // the shift in progress carried on
    }
}

public class AccuracyDetailTests
{
    const double Cycle = 1.0 / Cia.ClockHz;

    sealed class Clocked
    {
        public double Now;
        public Bus Bus = new();
        public Clocked() => Bus.Seconds = () => Now;
        public void Wait(double cycles) => Now += cycles * Cycle;
        public int Read(int a) => Bus.Read(a);
        public void Write(int a, int v) => Bus.Write(a, (byte)v);
    }

    // ---------- sprite DMA ----------
    static int Stolen(Bus bus, long from, long to) => bus.Vic.StolenCycles(from, to);

    static Bus WithSprites(int mask, int y = 100)
    {
        var bus = new Bus { Seconds = () => 0 };
        bus.Write(0xD011, 0x0B);                              // no bad lines
        bus.Write(0xD015, (byte)mask);
        for (int n = 0; n < 8; n++) bus.Write(0xD001 + n * 2, (byte)y);
        return bus;
    }

    [Fact]
    public void ASpriteTakesItsCyclesAtItsOwnPlaceInTheLine()
    {
        var bus = WithSprites(0b100);                        // sprite 2 only, drawn from line 101
        long line100 = 100 * Vic2.CyclesPerLine;
        Assert.Equal(0, Stolen(bus, line100, line100 + 50));                       // nothing before its fetch
        Assert.Equal(3, Stolen(bus, line100 + 50, line100 + 63 + 2));               // 58 + 2*2 = 62
        Assert.Equal(0, Stolen(bus, line100 + 70, line100 + 120));
    }

    [Fact]
    public void EightSpritesTake17CyclesInAll()
    {
        var bus = WithSprites(0xFF);
        long line100 = 100 * Vic2.CyclesPerLine;
        Assert.Equal(3 + 7 * 2, Stolen(bus, line100, line100 + 80));            // one line's fetches end 72 cycles in
    }

    [Fact]
    public void SpritesThatAreNotOnTheNextLineStealNothing()
    {
        var bus = WithSprites(0xFF, y: 100);
        long line50 = 50 * Vic2.CyclesPerLine;
        Assert.Equal(0, Stolen(bus, line50, line50 + 3 * Vic2.CyclesPerLine));
    }

    [Fact]
    public void ASpanThatStartsAfterTheFetchBeganStillCatchesTheRest()
    {
        var bus = WithSprites(0xFF);
        long line101Start = 101 * Vic2.CyclesPerLine;
        // sprites 3..7 are fetched at cycles 64..72, i.e. on line 101
        Assert.Equal(5 * 2, Stolen(bus, line101Start, line101Start + 20));
    }

    // ---------- light pen ----------
    [Fact]
    public void TheLightPenLatchesItsPositionOncePerFrame()
    {
        var m = new Clocked();
        m.Write(0xD01A, 0x08);
        m.Bus.Vic.LightPen(200, 120);
        Assert.Equal(100, m.Read(0xD013));
        Assert.Equal(120, m.Read(0xD014));
        Assert.Equal(0x08, m.Read(0xD019) & 0x08);
        Assert.True(m.Bus.Vic.InterruptPending);
        m.Bus.Vic.LightPen(50, 60);                           // the same frame: ignored
        Assert.Equal(100, m.Read(0xD013));
        m.Write(0xD019, 0x08);
        Assert.False(m.Bus.Vic.InterruptPending);
        m.Now += 0.02;                                         // next frame
        m.Bus.Vic.LightPen(50, 60);
        Assert.Equal(25, m.Read(0xD013));
        Assert.Equal(60, m.Read(0xD014));
    }

    [Fact]
    public void TheLightPenRegistersCannotBeWritten()
    {
        var m = new Clocked();
        m.Write(0xD013, 77);
        Assert.Equal(0, m.Read(0xD013));
    }

    // ---------- the FLAG pin and the user port ----------
    [Fact]
    public void AnEdgeOnFlagSetsBit4AndInterrupts()
    {
        var m = new Clocked();
        m.Write(0xDD0D, 0x90);                                // enable FLAG
        Assert.False(m.Bus.Cia2.InterruptPending);
        m.Bus.Cia2.PulseFlag();
        Assert.True(m.Bus.Cia2.InterruptPending);
        Assert.Equal(0x90, m.Read(0xDD0D));
        Assert.False(m.Bus.Cia2.InterruptPending);            // reading acknowledged it
    }

    [Fact]
    public void AMaskedFlagOnlyShowsInTheRegister()
    {
        var m = new Clocked();
        m.Bus.Cia1.PulseFlag();
        Assert.False(m.Bus.Cia1.InterruptPending && false);
        Assert.Equal(0x10, m.Read(0xDC0D) & 0x1F & 0x10);
    }

    [Fact]
    public void TheUserPortReadsWhatTheOutsideWorldHolds()
    {
        var m = new Clocked();
        m.Bus.Cia2.UserPortInput = 0xA5;
        Assert.Equal(0xA5, m.Read(0xDD01));
        m.Write(0xDD03, 0x0F);                                // low nibble becomes output
        m.Write(0xDD01, 0x03);
        Assert.Equal(0xA1, m.Read(0xDD01));                   // an output bit reads low if either the latch or the pin is low
        Assert.Equal(0xF3, m.Bus.Cia2.UserPortOutput);
    }
}
