using C64Basic.Core.Machine;

namespace C64Basic.Tests;

/// <summary>Per-raster-line drawing: the picture shows what the beam saw while registers changed.</summary>
public class VicRasterTests
{
    static uint P(int color) => Vic2.Palette[color];

    /// <summary>A machine with a clock the test moves by raster line and cycle (frame 0 is lines 0-311).</summary>
    sealed class Beam
    {
        public double Now;
        public readonly Bus Bus;

        public Beam()
        {
            Bus = new Bus { Seconds = () => Now };
            Array.Fill(Bus.Ram, (byte)32, 1024, 1000);          // blank screen
            At(0, 1);
        }

        public Beam At(int line, int cycle = 1) { Now = Vic2.SecondsAt(line, cycle); return this; }
        public Beam Write(int address, int value) { Bus.Write(address, (byte)value); return this; }

        /// <summary>The picture of the frame that just finished: move into the next frame and render.</summary>
        public uint[] Frame()
        {
            At(312 + 100);
            var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
            Bus.Vic.Render(frame);
            return frame;
        }
    }

    /// <summary>The pixel at frame x, raster line.</summary>
    static uint Px(uint[] frame, int x, int line) => frame[(line - 15) * Vic2.FrameWidth + x];

    [Fact]
    public void ABorderColourChangeSplitsTheFrameAtItsLine()
    {
        var beam = new Beam();
        beam.At(100, 1).Write(0xD020, 2);
        var frame = beam.Frame();
        Assert.Equal(P(14), Px(frame, 5, 99));
        Assert.Equal(P(2), Px(frame, 5, 100));
        Assert.Equal(P(2), Px(frame, 5, 280));
        Assert.Equal(P(14), Px(frame, 5, 20));
    }

    [Fact]
    public void AWriteInTheMiddleOfALineAffectsOnlyTheRestOfIt()
    {
        var beam = new Beam();
        beam.At(20, 30).Write(0xD020, 2);                   // line 20 is in the top border
        var frame = beam.Frame();
        Assert.Equal(P(14), Px(frame, 100, 20));            // cycle 30 starts at x = (30 - 13) * 8 = 136
        Assert.Equal(P(2), Px(frame, 300, 20));
        Assert.Equal(P(2), Px(frame, 100, 21));
    }

    [Fact]
    public void ColourBarsFromBackgroundWritesEveryLine()
    {
        var beam = new Beam();
        for (int line = 60; line < 70; line++) beam.At(line, 1).Write(0xD021, line % 16);
        var frame = beam.Frame();
        for (int line = 60; line < 70; line++) Assert.Equal(P(line % 16), Px(frame, 100, line));
        Assert.Equal(P(69 % 16), Px(frame, 100, 100));      // the last bar's colour stays for the rest of the frame
    }

    [Fact]
    public void TheLastWriteStaysForTheRestOfTheFrame()
    {
        var beam = new Beam();
        beam.At(60, 1).Write(0xD021, 7);
        var frame = beam.Frame();
        Assert.Equal(P(6), Px(frame, 100, 59));
        Assert.Equal(P(7), Px(frame, 100, 60));
        Assert.Equal(P(7), Px(frame, 100, 240));
    }

    [Fact]
    public void TheFrameShownIsTheLastCompleteOne()
    {
        var beam = new Beam();
        beam.At(100).Write(0xD021, 2);                      // during frame 0
        beam.At(312 + 100).Write(0xD021, 3);                // during frame 1, still being drawn
        beam.At(312 + 200);
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        beam.Bus.Vic.Render(frame);
        Assert.Equal(P(2), Px(frame, 100, 200));            // frame 0 as it ended, without the later write
        beam.At(2 * 312 + 10);
        beam.Bus.Vic.Render(frame);
        Assert.Equal(P(2), Px(frame, 100, 90));             // frame 1 started with frame 0's final colour...
        Assert.Equal(P(3), Px(frame, 100, 110));            // ...and changed at line 100
    }

    [Fact]
    public void ThePictureStartsFromTheRegistersOfTheFrameBefore()
    {
        var beam = new Beam();
        beam.At(10).Write(0xD020, 5);
        beam.At(312 * 3 + 40);                              // frames 1 and 2 passed without writes
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        beam.Bus.Vic.Render(frame);
        Assert.Equal(P(5), Px(frame, 5, 20));
    }

    // ---------- the border tricks ----------
    [Fact]
    public void TheBottomBorderStaysShutNormally()
    {
        var frame = new Beam().Frame();
        Assert.Equal(P(14), Px(frame, 100, 252));
        Assert.Equal(P(6), Px(frame, 100, 250));
    }

    [Fact]
    public void SwitchingTo24RowsAtTheRightLineOpensTheBottomBorder()
    {
        var beam = new Beam();
        beam.At(250, 20).Write(0xD011, 0x13);               // 24 rows before the 25-row comparison at line 251
        var frame = beam.Frame();
        Assert.Equal(P(6), Px(frame, 100, 252));            // the display area carries on past line 251
        Assert.Equal(P(6), Px(frame, 100, 270));
    }

    [Fact]
    public void ABorderThatWasOpenedClosesAgainNextFrame()
    {
        var beam = new Beam();
        beam.At(250, 20).Write(0xD011, 0x13);
        beam.At(300).Write(0xD011, 0x1B);                   // back to 25 rows after the check
        var frame = beam.Frame();
        Assert.Equal(P(6), Px(frame, 100, 270));
        beam.At(2 * 312 + 100);
        var next = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        beam.Bus.Vic.Render(next);                          // frame 1 had no tricks
        Assert.Equal(P(14), Px(next, 100, 270));
    }

    [Fact]
    public void TwentyFourRowsHideTheFirstAndLastLineOfTheWindow()
    {
        var beam = new Beam().Write(0xD011, 0x13);
        var frame = beam.Frame();
        Assert.Equal(P(14), Px(frame, 100, 54));
        Assert.Equal(P(6), Px(frame, 100, 55));
        Assert.Equal(P(6), Px(frame, 100, 246));
        Assert.Equal(P(14), Px(frame, 100, 247));
    }

    [Fact]
    public void SwitchingTo38ColumnsAtTheRightCycleOpensTheSideBorder()
    {
        var beam = new Beam();
        for (int line = 60; line < 200; line++)
        {
            beam.At(line, 56).Write(0xD016, 0xC0);         // 38 columns from cycle 57: skips both right-edge comparisons
            beam.At(line, 58).Write(0xD016, 0xC8);
        }
        var frame = beam.Frame();
        Assert.Equal(P(6), Px(frame, 365, 100));            // no right border
        Assert.Equal(P(14), Px(frame, 365, 59));            // lines outside the trick keep theirs
        Assert.Equal(P(14), Px(frame, 365, 250));
    }

    [Fact]
    public void WithoutTheTrickTheSideBordersShow()
    {
        var frame = new Beam().Frame();
        Assert.Equal(P(14), Px(frame, 365, 100));
        Assert.Equal(P(14), Px(frame, 5, 100));
        Assert.Equal(P(6), Px(frame, 100, 100));
    }

    [Fact]
    public void ThirtyEightColumnsHideSevenAndNinePixels()
    {
        var beam = new Beam().Write(0xD016, 0xC0);
        beam.Bus.Ram[1024] = 160;                           // a solid character in the top left
        beam.Bus.Ram[1062] = 160;                           // and the last column but one (display x 304-311)
        beam.Bus.Color.Data[0] = 5;
        beam.Bus.Color.Data[38] = 5;
        var frame = beam.Frame();
        Assert.Equal(P(14), Px(frame, 32 + 6, 51));         // display x 0-6 are hidden by the border
        Assert.Equal(P(5), Px(frame, 32 + 7, 51));          // x 7 shows the character
        Assert.Equal(P(5), Px(frame, 32 + 310, 51));        // that character is visible up to display x 310
        Assert.Equal(P(14), Px(frame, 32 + 311, 51));       // and hidden from 311
    }

    // ---------- scrolling ----------
    [Fact]
    public void XScrollShiftsTheScreenToTheRight()
    {
        var beam = new Beam().Write(0xD016, 0xC8 | 3);
        beam.Bus.Ram[1024] = 160;                           // solid character: all pixels set
        beam.Bus.Color.Data[0] = 5;
        var frame = beam.Frame();
        Assert.Equal(P(6), Px(frame, 32 + 2, 51));          // the first three pixels are background
        Assert.Equal(P(5), Px(frame, 32 + 3, 51));
        Assert.Equal(P(5), Px(frame, 32 + 10, 51));
        Assert.Equal(P(6), Px(frame, 32 + 11, 51));
    }

    [Fact]
    public void YScrollShiftsTheScreenDown()
    {
        var beam = new Beam().Write(0xD011, 0x1B + 1);      // yscroll 4: one line lower than the default 3
        beam.Bus.Ram[1024] = 160;
        beam.Bus.Color.Data[0] = 5;
        var frame = beam.Frame();
        Assert.Equal(P(6), Px(frame, 32 + 3, 51));          // the first line of the character is now at 52
        Assert.Equal(P(5), Px(frame, 32 + 3, 52));
        Assert.Equal(P(5), Px(frame, 32 + 3, 59));
        Assert.Equal(P(6), Px(frame, 32 + 3, 60));
    }

    // ---------- sprites ----------
    static void SolidSprite(Bus bus, int pointer = 13)
    {
        for (int i = 0; i < 63; i++) bus.Ram[pointer * 64 + i] = 0xFF;
    }

    [Fact]
    public void ASpriteReusedDownTheScreenAppearsTwice()
    {
        var beam = new Beam();
        SolidSprite(beam.Bus);
        beam.Bus.Ram[2040] = 13;
        beam.Write(0xD000, 100).Write(0xD001, 60).Write(0xD015, 1).Write(0xD027, 1);
        beam.At(100, 1).Write(0xD001, 150);                 // after the first copy is drawn, move it down
        var frame = beam.Frame();
        Assert.Equal(P(1), Px(frame, 100 + 8 + 5, 70));     // first position: lines 61-81
        Assert.Equal(P(6), Px(frame, 100 + 8 + 5, 120));    // gap
        Assert.Equal(P(1), Px(frame, 100 + 8 + 5, 160));    // second position: lines 151-171
    }

    [Fact]
    public void ASpriteAtYStartsOnTheNextLine()
    {
        var beam = new Beam();
        SolidSprite(beam.Bus);
        beam.Bus.Ram[2040] = 13;
        beam.Write(0xD000, 100).Write(0xD001, 100).Write(0xD015, 1);
        var frame = beam.Frame();
        Assert.Equal(P(6), Px(frame, 113, 100));
        Assert.Equal(P(1), Px(frame, 113, 101));
        Assert.Equal(P(1), Px(frame, 113, 121));
        Assert.Equal(P(6), Px(frame, 113, 122));
    }

    [Fact]
    public void ASpriteMovedMidwayShowsTheNewPositionFromThatLine()
    {
        var beam = new Beam();
        SolidSprite(beam.Bus);
        beam.Bus.Ram[2040] = 13;
        beam.Write(0xD001, 60).Write(0xD015, 1).Write(0xD000, 40);
        beam.At(65, 30).Write(0xD000, 200);                 // mid-sprite: the lower lines are drawn at the new x
        var frame = beam.Frame();
        Assert.Equal(P(1), Px(frame, 40 + 8 + 3, 62));
        Assert.Equal(P(6), Px(frame, 40 + 8 + 3, 70));
        Assert.Equal(P(1), Px(frame, 200 + 8 + 3, 70));
    }

    [Fact]
    public void SpriteCollisionsAreStillLatchedInLaterFrames()
    {
        var beam = new Beam();
        SolidSprite(beam.Bus);
        beam.Bus.Ram[2040] = 13; beam.Bus.Ram[2041] = 13;
        beam.Write(0xD000, 100).Write(0xD001, 100).Write(0xD002, 110).Write(0xD003, 105).Write(0xD015, 3);
        beam.At(5 * 312 + 40);
        Assert.Equal(3, beam.Bus.Read(0xD01E));
    }

    // ---------- the raster counter ----------
    [Fact]
    public void TheRasterInterruptFiresAtTheStartOfItsLine()
    {
        var beam = new Beam();
        beam.Write(0xD012, 100).Write(0xD01A, 1);
        beam.At(90);
        Assert.Equal(0x70, beam.Bus.Read(0xD019));
        beam.At(99, 63);
        Assert.Equal(0x70, beam.Bus.Read(0xD019));
        beam.At(100, 2);
        Assert.Equal(0xF1, beam.Bus.Read(0xD019));
    }

    [Fact]
    public void TheRasterInterruptRepeatsEveryFrame()
    {
        var beam = new Beam();
        beam.Write(0xD012, 50);
        beam.At(60);
        beam.Bus.Read(0xD019);
        beam.Write(0xD019, 1);
        beam.At(312 + 40);
        Assert.Equal(0, beam.Bus.Read(0xD019) & 1);
        beam.At(312 + 51);
        Assert.Equal(1, beam.Bus.Read(0xD019) & 1);
    }

    [Fact]
    public void ASkippedFrameStillCountsAsACrossing()
    {
        var beam = new Beam();
        beam.Write(0xD012, 200);
        beam.At(10);
        beam.Bus.Read(0xD019);
        beam.At(3 * 312 + 5);                               // the host paused for three frames
        Assert.Equal(1, beam.Bus.Read(0xD019) & 1);
    }

    [Fact]
    public void TheCycleWithinTheLineIsReported()
    {
        var beam = new Beam().At(100, 30);
        Assert.Equal(30, beam.Bus.Vic.CycleInLine);
        Assert.Equal(100, beam.Bus.Vic.Raster);
    }

    [Fact]
    public void ALongHistoryOfWritesIsFoldedWithoutLosingTheState()
    {
        var beam = new Beam();
        for (int i = 0; i < 30000; i++) beam.At(i, 1).Write(0xD020, i % 16);   // a write on every raster line for 96 frames
        beam.At(312 * 100);
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        beam.Bus.Vic.Render(frame);
        Assert.Equal(P(29999 % 16), Px(frame, 5, 20));      // the picture is the last frame, with the last colour
    }
}
