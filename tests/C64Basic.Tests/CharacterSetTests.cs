using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class CharacterSetTests
{
    static byte[] Glyph(byte[] rom, int set, int code) => rom[(set * 2048 + code * 8)..][..8];

    static Bus Still() => new Bus { Seconds = () => 0 };

    static uint[] Render(Bus bus)
    {
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        bus.Vic.Render(frame);
        return frame;
    }

    [Fact]
    public void EveryGraphicsGlyphIsDrawnExceptTheSpace()
    {
        var rom = CharRom.CreateDefault();
        for (int set = 0; set < 2; set++)
            for (int code = 64; code < 128; code++)
                if (code != 96) Assert.NotEqual(new byte[8], Glyph(rom, set, code));
        Assert.Equal(new byte[8], Glyph(rom, 0, 96));
    }

    [Fact]
    public void TheReversedHalfIsTheInverse()
    {
        var rom = CharRom.CreateDefault();
        for (int set = 0; set < 2; set++)
            for (int code = 0; code < 128; code++)
                Assert.Equal(Glyph(rom, set, code).Select(b => (byte)~b), Glyph(rom, set, code + 128));
    }

    [Fact]
    public void TheLowerCaseSetHasLowerCaseLettersAndCapitals()
    {
        var rom = CharRom.CreateDefault();
        for (int i = 1; i <= 26; i++)
        {
            Assert.NotEqual(Glyph(rom, 0, i), Glyph(rom, 1, i));          // a differs from A
            Assert.Equal(Glyph(rom, 0, i), Glyph(rom, 1, 64 + i));         // the capitals sit at 65-90
        }
        Assert.Equal(Glyph(rom, 0, 32), Glyph(rom, 1, 32));                // space and digits are shared
        Assert.Equal(Glyph(rom, 0, 49), Glyph(rom, 1, 49));
    }

    [Fact]
    public void LowerCaseLettersAreDistinctShapes()
    {
        var rom = CharRom.CreateDefault();
        var shapes = Enumerable.Range(1, 26).Select(i => Convert.ToHexString(Glyph(rom, 1, i))).ToList();
        Assert.Equal(26, shapes.Distinct().Count());
    }

    [Fact]
    public void TheVicDrawsTheSelectedSet()
    {
        var bus = Still();
        Array.Fill(bus.Ram, (byte)32, 1024, 1000);
        bus.Ram[1024] = 1;                                                 // screen code 1
        uint upper = Render(bus)[36 * Vic2.FrameWidth + 32 + 3];           // pixel (3,0): the top of an A
        Assert.Equal(Vic2.Palette[14], upper);

        bus.Write(0xD018, 0x17);                                           // lower-case set
        uint lower = Render(bus)[36 * Vic2.FrameWidth + 32 + 3];           // a has nothing in its first row
        Assert.Equal(Vic2.Palette[6], lower);
    }

    [Fact]
    public void ACharacterRomDumpReplacesTheBuiltInSet()
    {
        var bus = Still();
        var rom = new byte[4096];
        rom[8] = 0xFF;                                                      // screen code 1, first row, solid
        bus.LoadCharacterRom(rom);
        Array.Fill(bus.Ram, (byte)32, 1024, 1000);
        bus.Ram[1024] = 1;
        Assert.Equal(Vic2.Palette[14], Render(bus)[36 * Vic2.FrameWidth + 32 + 7]);

        bus.Write(1, 0x33);                                                 // character ROM visible to the CPU
        Assert.Equal(0xFF, bus.Read(53248 + 8));
        Assert.Equal(0, bus.Read(53248 + 9));
    }

    [Fact]
    public void ARomOfTheWrongSizeIsRefused()
    {
        var bus = new Bus();
        Assert.Throws<ArgumentException>(() => bus.LoadCharacterRom(new byte[2048]));
        Assert.Equal(4096, bus.CharacterRom.Length);
    }

}
