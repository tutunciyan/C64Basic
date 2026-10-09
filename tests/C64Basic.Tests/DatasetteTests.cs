using C64Basic.Core.Disk;
using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>The cassette port in ROM mode: the real KERNAL tape routines read pulses from a TAP and record pulses for a SAVE.</summary>
public class DatasetteTests
{
    // 10 PRINT"TAPE OK"
    static readonly byte[] Program =
    {
        0x01, 0x08, 0x10, 0x08, 0x0A, 0x00, 0x99, 0x22, (byte)'T', (byte)'A', (byte)'P', (byte)'E', (byte)' ', (byte)'O', (byte)'K', 0x22, 0x00, 0x00, 0x00,
    };

    [Fact]
    public void PulsesAreKeptAsTheirLengthsInCycles()
    {
        var tape = new TapImage();
        tape.AppendPulses(new[] { 384, 528, 3000, 20000 });          // the last two do not fit a byte of 8-cycle units and are stored in full
        Assert.Equal(new[] { 384, 528, 3000, 20000 }, tape.PulseCycles());
    }

    [Fact]
    public void TheRecorderFiresOneEdgePerPulseWhileThePlayingMotorRuns()
    {
        var tape = new TapImage();
        tape.AppendPulses(new[] { 400, 400, 400 });
        var d = new Datasette();
        d.Insert(tape);
        int edges = 0;
        d.Update(0, motor: false, writeLine: true, () => edges++);
        d.Update(10_000, false, true, () => edges++);
        Assert.Equal(0, edges);                                      // the motor is off: the tape stands still
        d.Update(10_000, true, true, () => edges++);                 // started at 10,000: the first edge is 400 cycles on
        d.Update(10_399, true, true, () => edges++);
        Assert.Equal(0, edges);
        d.Update(10_400, true, true, () => edges++);
        Assert.Equal(1, edges);
        d.Update(10_700, false, true, () => edges++);                // stopped 100 cycles before the next edge...
        d.Update(50_000, false, true, () => edges++);
        Assert.Equal(1, edges);
        d.Update(60_000, true, true, () => edges++);                 // ...which comes 100 cycles after it starts again
        d.Update(60_099, true, true, () => edges++);
        Assert.Equal(1, edges);
        d.Update(60_100, true, true, () => edges++);
        d.Update(60_500, true, true, () => edges++);
        Assert.Equal(3, edges);
        Assert.True(d.AtEnd);
    }

    [Fact]
    public void RecordedRisingEdgesOfTheWriteLineBecomePulsesWhenTheMotorStops()
    {
        var d = new Datasette();
        var tape = new TapImage();
        d.Insert(tape);
        long t = 1000;
        d.Update(900, true, false, () => { });                       // the line starts low
        foreach (int length in new[] { 0, 300, 400, 500, 400 })
        {
            t += length;
            d.Update(t, true, true, () => { });                      // the line goes high: a flux reversal
            d.Update(t + 50, true, false, () => { });
        }
        d.Update(t + 60, false, true, () => { });                    // motor off
        Assert.Equal(new[] { 304, 400, 504, 400 }, tape.PulseCycles());   // lengths rounded to units of 8 cycles
    }

    [RomFact]
    public void AProgramOnATapeLoadsAndRunsThroughTheRealKernal()
    {
        var roms = TestRoms.Find()!;
        var tape = new TapImage();
        tape.Write("HELLO", FileType.Prg, Program, replace: false);
        var m = new RomMachine(roms);
        m.RunSeconds(3.0);
        m.MountTape(tape);
        m.Type("LOAD\r");                                            // the default device is the tape
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("FOUND HELLO"), 30), m.ScreenText());
        Assert.True(m.RunUntil(() => m.ScreenText().Split('\n').Last().Trim() == "READY." && m.Bus.Ram[0x2D] > 0x10, 120), m.ScreenText());
        Assert.DoesNotContain("ERROR", m.ScreenText());
        m.Type("RUN\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("TAPE OK"), 10), m.ScreenText());
        Assert.Null(m.HaltReason);
    }

    [RomFact]
    public void ASaveToTapeIsRecordedAsPulsesThatReadBackAsTheProgram()
    {
        var roms = TestRoms.Find()!;
        var tape = new TapImage();
        var m = new RomMachine(roms);
        m.RunSeconds(3.0);
        m.MountTape(tape);
        m.Type("10 PRINT\"RECORDED\"\rSAVE\"REC\"\r");
        Assert.True(m.RunUntil(() => tape.Directory().Any(e => e.Name == "REC"), 120), m.ScreenText());
        Assert.Null(m.HaltReason);

        // and the real KERNAL reads its own recording back
        m.Tape.Rewind();
        m.Type("NEW\rLOAD\"REC\"\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("FOUND REC"), 40), m.ScreenText());
        Assert.True(m.RunUntil(() => m.Bus.Ram[0x2D] > 0x10 && m.ScreenText().EndsWith("READY."), 120), m.ScreenText());
        m.Type("RUN\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("RECORDED"), 10), m.ScreenText());
    }
}
