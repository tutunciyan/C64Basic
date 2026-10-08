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

    [Fact]
    public void ChangingTheRomDoesNotAffectOtherMachines()
    {
        var a = new Bus();
        var b = new Bus();
        a.LoadCharacterRom(new byte[4096]);
        Assert.NotEqual(a.CharacterRom, b.CharacterRom);
    }

    // ---------- switching sets from the keyboard and BASIC ----------
    [Fact]
    public void ControlCodes14And142SwitchTheSet()
    {
        var bus = new Bus();
        var editor = new ScreenEditor(bus);
        Assert.False(editor.LowerCase);
        editor.Write("\u000e");
        Assert.True(editor.LowerCase);
        Assert.Equal(0x17, bus.Read(0xD018) & 0x1F);
        editor.Write("\u008e");
        Assert.False(editor.LowerCase);
        Assert.Equal(0x15, bus.Read(0xD018) & 0x1F);
    }

    [Fact]
    public void BasicCanSwitchWithChr()
    {
        var console = new ScreenConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine("PRINT CHR$(14);");
        Assert.Equal(2, interp.Bus.Read(0xD018) & 2);
        interp.ProcessLine("PRINT CHR$(142);");
        Assert.Equal(0, interp.Bus.Read(0xD018) & 2);
    }

    [Fact]
    public void ShiftedLettersAreGraphicsInTheFirstSet()
    {
        Assert.Equal(Petscii.ToChar(193), Petscii.ShiftedLetter('A'));
        Assert.Equal(Petscii.ToChar(193), Petscii.ShiftedLetter('a'));
        Assert.Equal(Petscii.ToChar(218), Petscii.ShiftedLetter('Z'));
        Assert.Equal('1', Petscii.ShiftedLetter('1'));
        Assert.Equal(65, ScreenEditor.ToScreenCode(Petscii.ShiftedLetter('A')));
        Assert.Equal(193, Petscii.ToCode(Petscii.ShiftedLetter('A')));
    }

    [Fact]
    public void ShiftedAndPlainLettersDifferOnScreen()
    {
        var bus = new Bus();
        var editor = new ScreenEditor(bus);
        editor.Write("a" + Petscii.ShiftedLetter('a'));
        Assert.Equal(1, bus.Ram[1024]);
        Assert.Equal(65, bus.Ram[1025]);
    }

    // ---------- quote mode ----------
    static (ScreenEditor Editor, Bus Bus) Make()
    {
        var bus = new Bus();
        return (new ScreenEditor(bus), bus);
    }

    [Fact]
    public void ControlCodesAreShownNotObeyedInsideQuotes()
    {
        var (editor, bus) = Make();
        editor.Write("\"\u0093\u001c\u0011\"");
        Assert.Equal(0, bus.Ram[212]);                     // the closing quote left quote mode
        Assert.Equal(34, bus.Ram[1024]);
        Assert.Equal(147 + 64, bus.Ram[1025]);             // reverse heart for CLR
        Assert.Equal(28 + 128, bus.Ram[1026]);             // reverse pound for red
        Assert.Equal(17 + 128, bus.Ram[1027]);             // reverse Q for cursor down
        Assert.Equal(34, bus.Ram[1028]);
        Assert.Equal(0, bus.Ram[214]);                     // the cursor never moved down, nothing was cleared
    }

    [Fact]
    public void QuoteModeFlagLivesInZeroPage()
    {
        var (editor, bus) = Make();
        editor.Write("\"");
        Assert.Equal(1, bus.Ram[212]);
        editor.Write("\n");
        Assert.Equal(0, bus.Ram[212]);
    }

    [Fact]
    public void ReturnEndsQuoteMode()
    {
        var (editor, bus) = Make();
        editor.Write("\"\n\u001c");
        Assert.Equal(2, bus.Ram[646] & 2);                 // obeyed again: the text colour is red now
    }

    [Fact]
    public void DeleteStillWorksInsideQuotes()
    {
        var (editor, bus) = Make();
        editor.Write("\"AB\u0014");
        Assert.Equal(1, bus.Ram[1024 + 1]);
        Assert.Equal(32, bus.Ram[1024 + 2]);
    }

    [Fact]
    public void TypedControlCodesInStringsReadBackAsTheCodes()
    {
        var (editor, _) = Make();
        editor.Write("PRINT\"\u0093\u001c\u0011\u009f\"");
        Assert.Equal("PRINT\"\u0093\u001c\u0011\u009f\"", editor.ReadLine(0, 0));
    }

    [Fact]
    public void ReverseLettersOutsideQuotesAreJustLetters()
    {
        var (editor, bus) = Make();
        editor.Write("\u0012A");                           // reverse A, no quote: screen code 129
        Assert.Equal(129, bus.Ram[1024]);
        Assert.Equal("A", editor.ReadLine(0, 0));
    }

    [Fact]
    public void ListingAProgramWithControlCodesInStringsShowsSymbols()
    {
        var console = new ScreenConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine("10 PRINT \"\u001cRED\"");
        interp.ProcessLine("LIST");
        Assert.Contains((byte)(28 + 128), interp.Bus.Ram[1024..2024]);   // the red code is drawn as a reverse pound
    }

    [Fact]
    public void TogglingFromTheConsoleSwitchesTheSet()
    {
        var console = new ScreenConsole();
        var bus = new Bus();
        console.Attach(bus);
        console.ToggleCharacterSet();
        Assert.Equal(2, bus.Read(0xD018) & 2);
        console.ToggleCharacterSet();
        Assert.Equal(0, bus.Read(0xD018) & 2);
    }
}
