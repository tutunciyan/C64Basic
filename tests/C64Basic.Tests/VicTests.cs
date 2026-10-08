using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class VicTests
{
    static uint P(int color) => Vic2.Palette[color];

    /// <summary>Colour of display pixel (x, y), inside the border.</summary>
    static uint At(uint[] frame, int x, int y) => frame[(y + 36) * Vic2.FrameWidth + 32 + x];

    /// <summary>A bus whose screen is filled with spaces, so every cell shows plain background.</summary>
    static Bus NewBus()
    {
        var bus = new Bus();
        Array.Fill(bus.Ram, (byte)32, 1024, 1000);
        return bus;
    }

    static uint[] Render(Bus bus)
    {
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        bus.Vic.Render(frame);
        return frame;
    }

    static void SpriteBlock(Bus bus, int pointer, byte fill)
    {
        for (int i = 0; i < 63; i++) bus.Ram[pointer * 64 + i] = fill;
    }

    static void EnableSprite(Bus bus, int n, int x, int y, int pointer = 13)
    {
        bus.Ram[2040 + n] = (byte)pointer;
        bus.Write(0xD000 + n * 2, (byte)(x & 0xFF));
        bus.Write(0xD001 + n * 2, (byte)y);
        bus.Write(0xD010, (byte)((bus.Read(0xD010) & ~(1 << n)) | (x >> 8 & 1) << n));
        bus.Write(0xD015, (byte)(bus.Read(0xD015) | 1 << n));
    }

    [Fact]
    public void TextModeDrawsCharacterAndBackground()
    {
        var bus = NewBus();
        bus.Ram[1024] = 160; // reversed space: all pixels set
        bus.Ram[1025] = 0;   // '@'
        var frame = Render(bus);
        Assert.Equal(P(14), At(frame, 0, 0));
        Assert.Equal(P(14), At(frame, 7, 7));
        Assert.Equal(P(6), At(frame, 8, 0));  // '@' has bit 7 clear in its first row
        Assert.Equal(P(14), At(frame, 10, 0)); // and bits 5-2 set (0x3C)
    }

    [Fact]
    public void FrameHasBorderAround()
    {
        var bus = NewBus();
        bus.Write(0xD020, 2);
        var frame = Render(bus);
        Assert.Equal(P(2), frame[0]);
        Assert.Equal(P(2), frame[Vic2.FrameWidth * Vic2.FrameHeight - 1]);
        Assert.Equal(P(6), At(frame, 100, 100));
    }

    [Fact]
    public void ColourRamSelectsForeground()
    {
        var bus = NewBus();
        bus.Ram[1024] = 160;
        bus.Write(0xD800, 5);
        Assert.Equal(P(5), At(Render(bus), 3, 3));
    }

    [Fact]
    public void ColumnAndRowSelectHideEdges()
    {
        var bus = NewBus();
        bus.Ram[1024] = 160;
        bus.Write(0xD800, 5);
        bus.Write(0xD016, 0xC0); // 38 columns
        bus.Write(0xD011, 0x13); // 24 rows
        var frame = Render(bus);
        Assert.Equal(P(5), At(frame, 7, 5));
        Assert.Equal(P(14), At(frame, 3, 5));  // border colour over the first 7 columns
        Assert.Equal(P(14), At(frame, 7, 2));  // and over the first 4 rows
        bus.Write(0xD020, 1);
        frame = Render(bus);
        Assert.Equal(P(1), At(frame, 3, 5));
        Assert.Equal(P(1), At(frame, 7, 2));
        Assert.Equal(P(6), At(frame, 100, 100));
    }

    [Fact]
    public void DisplayOffShowsBorderColour()
    {
        var bus = NewBus();
        bus.Write(0xD011, 0x0B);
        Assert.Equal(P(14), At(Render(bus), 100, 100));
    }

    [Fact]
    public void ExtendedColourUsesBackgroundRegisters()
    {
        var bus = NewBus();
        bus.Write(0xD011, 0x5B);
        bus.Write(0xD022, 2);
        bus.Ram[1024] = 0x40 | 32; // background colour 1, space
        var frame = Render(bus);
        Assert.Equal(P(2), At(frame, 0, 0));
        Assert.Equal(P(6), At(frame, 8, 0));
    }

    [Fact]
    public void MulticolourTextUsesColourBit3()
    {
        var bus = NewBus();
        bus.Write(0xD016, 0xD8);
        bus.Write(0xD022, 2);
        bus.Write(0xD023, 3);
        bus.Ram[1024] = 0;                    // '@' glyph, row 0 = 0x3C = 00 11 11 00
        bus.Write(0xD800, 8 | 5);             // multicolour, colour 5
        var frame = Render(bus);
        Assert.Equal(P(6), At(frame, 0, 0));  // 00 -> background
        Assert.Equal(P(5), At(frame, 2, 0));  // 11 -> colour & 7
        Assert.Equal(P(5), At(frame, 3, 0));  // pixels are doubled
        Assert.Equal(P(6), At(frame, 6, 0));
    }

    [Fact]
    public void HiresBitmapUsesScreenNibbles()
    {
        var bus = NewBus();
        bus.Write(0xD011, 0x3B);
        bus.Write(0xD018, 0x18);              // bitmap at 8192
        bus.Ram[8192] = 0x80;
        bus.Ram[1024] = 0x25;
        var frame = Render(bus);
        Assert.Equal(P(2), At(frame, 0, 0));
        Assert.Equal(P(5), At(frame, 1, 0));
    }

    [Fact]
    public void MulticolourBitmapPicksFromNibblesAndColourRam()
    {
        var bus = NewBus();
        bus.Write(0xD011, 0x3B);
        bus.Write(0xD016, 0xD8);
        bus.Write(0xD018, 0x18);
        bus.Ram[8192] = 0b01_10_11_00;
        bus.Ram[1024] = 0x25;
        bus.Write(0xD800, 7);
        var frame = Render(bus);
        Assert.Equal(P(2), At(frame, 0, 0));
        Assert.Equal(P(5), At(frame, 2, 0));
        Assert.Equal(P(7), At(frame, 4, 0));
        Assert.Equal(P(6), At(frame, 6, 0));
    }

    [Fact]
    public void CharacterSetPointerSelectsRam()
    {
        var bus = NewBus();
        bus.Write(0xD018, 0x1C);              // characters at 12288
        bus.Ram[1024] = 0;
        bus.Ram[12288] = 0x80;                // glyph 0, row 0
        var frame = Render(bus);
        Assert.Equal(P(14), At(frame, 0, 0));
        Assert.Equal(P(6), At(frame, 1, 0));
    }

    [Fact]
    public void SpriteAppearsAtItsCoordinate()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        EnableSprite(bus, 0, 24, 50);
        var frame = Render(bus);
        Assert.Equal(P(1), At(frame, 0, 0));
        Assert.Equal(P(1), At(frame, 23, 20));
        Assert.Equal(P(6), At(frame, 24, 0));
        Assert.Equal(P(6), At(frame, 0, 21));
    }

    [Fact]
    public void SpriteUsesNinthXBit()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        EnableSprite(bus, 0, 300 + 24, 50);   // needs bit 8 of X
        Assert.Equal(P(1), At(Render(bus), 300, 0));
    }

    [Fact]
    public void SpriteIsClippedByBorder()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        EnableSprite(bus, 0, 10, 50);         // starts 14 pixels left of the window
        var frame = Render(bus);
        Assert.Equal(P(1), At(frame, 0, 0));
        Assert.Equal(P(1), At(frame, 9, 0));
        Assert.Equal(P(6), At(frame, 10, 0));
        Assert.Equal(P(14), frame[(36 + 0) * Vic2.FrameWidth + 20]); // border untouched
    }

    [Fact]
    public void ExpandedSpriteIsTwiceAsBig()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        EnableSprite(bus, 0, 24, 50);
        bus.Write(0xD01D, 1);
        bus.Write(0xD017, 1);
        var frame = Render(bus);
        Assert.Equal(P(1), At(frame, 47, 41));
        Assert.Equal(P(6), At(frame, 48, 0));
        Assert.Equal(P(6), At(frame, 0, 42));
    }

    [Fact]
    public void MulticolourSpriteUsesSharedColours()
    {
        var bus = NewBus();
        bus.Ram[13 * 64] = 0b01_10_11_00;
        EnableSprite(bus, 0, 24, 50);
        bus.Write(0xD01C, 1);
        bus.Write(0xD025, 2);
        bus.Write(0xD026, 5);
        bus.Write(0xD027, 7);
        var frame = Render(bus);
        Assert.Equal(P(2), At(frame, 0, 0));
        Assert.Equal(P(7), At(frame, 2, 0));
        Assert.Equal(P(5), At(frame, 4, 0));
        Assert.Equal(P(6), At(frame, 6, 0)); // transparent
    }

    [Fact]
    public void LowerNumberedSpriteIsInFront()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        EnableSprite(bus, 0, 24, 50);
        EnableSprite(bus, 1, 24, 50);
        bus.Write(0xD027, 3);
        bus.Write(0xD028, 4);
        Assert.Equal(P(3), At(Render(bus), 5, 5));
    }

    [Fact]
    public void SpriteGoesBehindForegroundWhenPriorityBitSet()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        bus.Ram[1024] = 160;                  // foreground block at 0..7
        EnableSprite(bus, 0, 24, 50);
        bus.Write(0xD01B, 1);
        var frame = Render(bus);
        Assert.Equal(P(14), At(frame, 3, 3));   // behind the character
        Assert.Equal(P(1), At(frame, 12, 3));   // in front of background
    }

    [Fact]
    public void SpriteSpriteCollisionLatchesUntilRead()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        EnableSprite(bus, 0, 100, 100);
        EnableSprite(bus, 3, 110, 105);
        Assert.Equal(0b1001, bus.Read(0xD01E));
        Assert.Equal(0b1001, bus.Read(0xD01E)); // still overlapping: latched again
        bus.Write(0xD015, 0);
        Assert.Equal(0, bus.Read(0xD01E));
    }

    [Fact]
    public void SeparateSpritesDoNotCollide()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        EnableSprite(bus, 0, 100, 100);
        EnableSprite(bus, 1, 200, 100);
        Assert.Equal(0, bus.Read(0xD01E));
    }

    [Fact]
    public void SpriteBackgroundCollision()
    {
        var bus = NewBus();
        SpriteBlock(bus, 13, 0xFF);
        bus.Ram[1024] = 160;
        EnableSprite(bus, 2, 24, 50);
        EnableSprite(bus, 5, 200, 150);
        Assert.Equal(0b100, bus.Read(0xD01F));
    }

    [Fact]
    public void SpriteDataComesFromTheSelectedVicBank()
    {
        var bus = NewBus();
        bus.Write(0xDD00, 0x96);              // bank 1: 16384-32767
        bus.Ram[0x4000 + 1024 + 1016] = 5;    // sprite 0 pointer
        for (int i = 0; i < 63; i++) bus.Ram[0x4000 + 5 * 64 + i] = 0xFF;
        bus.Write(0xD000, 24);
        bus.Write(0xD001, 50);
        bus.Write(0xD015, 1);
        Assert.Equal(P(1), At(Render(bus), 0, 0));
    }

    [Fact]
    public void RasterCounterFollowsTheClock()
    {
        var bus = NewBus();
        bus.Vic.Seconds = () => 100.5 / Vic2.RasterLines / Vic2.FramesPerSecond;
        Assert.Equal(100, bus.Read(0xD012));
        bus.Vic.Seconds = () => 270.5 / Vic2.RasterLines / Vic2.FramesPerSecond;
        Assert.Equal(270 & 0xFF, bus.Read(0xD012));
        Assert.Equal(0x80, bus.Read(0xD011) & 0x80); // bit 8 of the raster line
    }

    [Fact]
    public void RasterCompareSetsInterruptFlag()
    {
        var bus = NewBus();
        double line = 10.5;
        bus.Vic.Seconds = () => line / Vic2.RasterLines / Vic2.FramesPerSecond;
        bus.Write(0xD012, 50);
        bus.Write(0xD01A, 1);
        Assert.Equal(0x70, bus.Read(0xD019));   // not reached yet
        line = 60.5;
        Assert.Equal(0xF1, bus.Read(0xD019));   // reached; interrupt would fire
        bus.Write(0xD019, 1);                   // acknowledge
        Assert.Equal(0x70, bus.Read(0xD019));
    }

    [Fact]
    public void RasterCompareHandlesWrapAround()
    {
        var bus = NewBus();
        double line = 300.5;
        bus.Vic.Seconds = () => line / Vic2.RasterLines / Vic2.FramesPerSecond;
        bus.Read(0xD019);                       // the beam is now at line 300
        bus.Write(0xD012, 5);
        Assert.Equal(0x70, bus.Read(0xD019));
        line = 312 + 10.5;
        Assert.Equal(1, bus.Read(0xD019) & 1);
    }

    [Fact]
    public void ControlRegisterKeepsWrittenBitsApartFromRaster()
    {
        var bus = NewBus();
        bus.Vic.Seconds = () => 0.5 / Vic2.RasterLines / Vic2.FramesPerSecond;
        bus.Write(0xD011, 0x9B);
        Assert.Equal(0x1B, bus.Read(0xD011)); // bit 7 is the raster line, which is below 256
    }

    [Fact]
    public void BasicCanPlaySpriteGame()
    {
        // 10 POKE the sprite pointer and registers from BASIC, then check a collision
        var (_, interp, _) = Basic.Session(
            "FOR I=0 TO 62:POKE 832+I,255:NEXT",
            "POKE 2040,13:POKE 2041,13",
            "POKE 53248,100:POKE 53249,100:POKE 53250,105:POKE 53251,105",
            "POKE 53269,3");
        Assert.Equal(3, interp.Bus.Read(0xD01E));
        Assert.Equal(" 3 \n", Basic.Run("POKE 53269,3:PRINT PEEK(53269)"));
    }
}
