using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class ScreenEditorTests
{
    static (ScreenEditor Editor, Bus Bus) Make()
    {
        var bus = new Bus();
        return (new ScreenEditor(bus), bus);
    }

    static string Row(Bus bus, int row) =>
        string.Concat(Enumerable.Range(0, 40).Select(c => ScreenEditor.FromScreenCode(bus.Ram[1024 + row * 40 + c]))).TrimEnd();

    [Fact]
    public void StartsWithAClearScreen()
    {
        var (_, bus) = Make();
        Assert.All(bus.Ram[1024..2024], b => Assert.Equal(32, b));
        Assert.Equal(0, bus.Ram[214]);
        Assert.Equal(0, bus.Ram[211]);
    }

    [Fact]
    public void PrintsTextAtTheCursorAndMovesIt()
    {
        var (editor, bus) = Make();
        editor.Write("HELLO");
        Assert.Equal(new byte[] { 8, 5, 12, 12, 15 }, bus.Ram[1024..1029]);   // screen codes: A = 1
        Assert.Equal(5, bus.Ram[211]);
        Assert.Equal("HELLO", Row(bus, 0));
    }

    [Fact]
    public void LowerCaseShowsAsUpperCaseLikeAC64()
    {
        var (editor, bus) = Make();
        editor.Write("hello");
        Assert.Equal("HELLO", Row(bus, 0));
    }

    [Fact]
    public void NewlineMovesToTheNextRow()
    {
        var (editor, bus) = Make();
        editor.Write("A\nB");
        Assert.Equal("A", Row(bus, 0));
        Assert.Equal("B", Row(bus, 1));
        Assert.Equal(1, bus.Ram[214]);
    }

    [Fact]
    public void TextColourGoesToColourRam()
    {
        var (editor, bus) = Make();
        editor.Write("\u0005X\u001cY");   // white, then red
        Assert.Equal(1, bus.Color.Data[0]);
        Assert.Equal(2, bus.Color.Data[1]);
        Assert.Equal(2, bus.Ram[646]);
    }

    [Fact]
    public void ReverseVideoSetsTheHighBit()
    {
        var (editor, bus) = Make();
        editor.Write("\u0012A\u0092B");
        Assert.Equal(129, bus.Ram[1024]);
        Assert.Equal(2, bus.Ram[1025]);
    }

    [Fact]
    public void ReverseEndsAtNewline()
    {
        var (editor, bus) = Make();
        editor.Write("\u0012A\nB");
        Assert.Equal(2, bus.Ram[1024 + 40]);
    }

    [Fact]
    public void CursorKeysMoveWithoutDrawing()
    {
        var (editor, bus) = Make();
        editor.Write("\u0011\u0011\u001d\u001d\u001d");
        Assert.Equal(2, bus.Ram[214]);
        Assert.Equal(3, bus.Ram[211]);
        editor.Write("\u0091\u009d");
        Assert.Equal(1, bus.Ram[214]);
        Assert.Equal(2, bus.Ram[211]);
        editor.Write("\u0013");
        Assert.Equal(0, bus.Ram[214]);
        Assert.Equal(0, bus.Ram[211]);
        Assert.All(bus.Ram[1024..2024], b => Assert.Equal(32, b));
    }

    [Fact]
    public void ClearHomesAndBlanksTheScreen()
    {
        var (editor, bus) = Make();
        editor.Write("JUNK\n\n\u0093");
        Assert.All(bus.Ram[1024..2024], b => Assert.Equal(32, b));
        Assert.Equal(0, bus.Ram[214]);
    }

    [Fact]
    public void LongLinesWrapAtColumn40()
    {
        var (editor, bus) = Make();
        editor.Write(new string('A', 45));
        Assert.Equal(1, bus.Ram[214]);
        Assert.Equal(5, bus.Ram[211]);
        Assert.Equal(new string('A', 5), Row(bus, 1));
    }

    [Fact]
    public void WritingPastTheBottomScrolls()
    {
        var (editor, bus) = Make();
        for (int i = 0; i < 25; i++) editor.Write($"LINE{i:00}\n");
        // 25 lines were printed and the cursor is on a 26th row: the first line has scrolled off
        Assert.Equal("LINE01", Row(bus, 0));
        Assert.Equal("LINE24", Row(bus, 23));
        Assert.Equal("", Row(bus, 24));
        Assert.Equal(24, bus.Ram[214]);
    }

    [Fact]
    public void DeleteRemovesTheCharacterBeforeTheCursor()
    {
        var (editor, bus) = Make();
        editor.Write("ABCD\u009d\u009d\u0014");   // cursor between B and C, then DEL
        Assert.Equal("ACD", Row(bus, 0));
        Assert.Equal(1, bus.Ram[211]);
    }

    [Fact]
    public void DeleteAtTheTopLeftDoesNothing()
    {
        var (editor, bus) = Make();
        editor.Write("\u0014");
        Assert.Equal(0, bus.Ram[211]);
    }

    [Fact]
    public void InsertOpensAGap()
    {
        var (editor, bus) = Make();
        editor.Write("ABCD\u009d\u009d\u009d\u0094");
        Assert.Equal("A BCD", Row(bus, 0));
    }

    [Fact]
    public void ReadLineReturnsTypedTextAndStartsANewLine()
    {
        var (editor, bus) = Make();
        editor.Write("PRINT \"HI\"");
        Assert.Equal("PRINT \"HI\"", editor.ReadLine(0, 0));
        Assert.Equal(1, bus.Ram[214]);
        Assert.Equal(0, bus.Ram[211]);
    }

    [Fact]
    public void ReadLineSkipsThePrompt()
    {
        var (editor, _) = Make();
        editor.Write("? 42");
        Assert.Equal("42", editor.ReadLine(0, 2));
    }

    [Fact]
    public void ReadingAnOldLineAfterMovingTheCursorUpReadsItWholeFromItsStart()
    {
        var (editor, _) = Make();
        editor.Write("10 PRINT 1\nREADY.\n");
        editor.Write("\u0091\u0091\u001d\u001d");     // cursor up twice, then right twice, on the program line
        Assert.Equal("10 PRINT 1", editor.ReadLine(2, 0));
    }

    [Fact]
    public void WrappedLinesAreReadAsOne()
    {
        var (editor, _) = Make();
        string text = new string('A', 50);
        editor.Write(text);
        Assert.Equal(text, editor.ReadLine(0, 0));
    }

    [Fact]
    public void LineStartingBelowTheInputRowIsReadFromItsOwnStart()
    {
        var (editor, _) = Make();
        editor.Write("OLD LINE\n");
        editor.Write("\u0091");                        // back up onto it, the prompt row is now row 1
        Assert.Equal("OLD LINE", editor.ReadLine(1, 0));
    }

    [Fact]
    public void TrailingSpacesAreTrimmed()
    {
        var (editor, _) = Make();
        editor.Write("A   ");
        Assert.Equal("A", editor.ReadLine(0, 0));
    }

    [Fact]
    public void ReadsBackGraphicsAndSpecialCharacters()
    {
        var (editor, _) = Make();
        editor.Write("A@£");
        Assert.Equal("A@£", editor.ReadLine(0, 0));
    }

    [Fact]
    public void ScreenCodesRoundTripThroughCharacters()
    {
        for (int code = 0; code < 128; code++)
        {
            char c = ScreenEditor.FromScreenCode(code);
            Assert.Equal(code, ScreenEditor.ToScreenCode(c));
        }
    }

    [Fact]
    public void PokedScreenRamIsReadBack()
    {
        var (editor, bus) = Make();
        bus.Ram[1024] = 1; bus.Ram[1025] = 2;
        Assert.Equal("AB", editor.ReadLine(0, 0));
    }

    [Fact]
    public void CursorPokesMoveThePrintPosition()
    {
        var (editor, bus) = Make();
        bus.Write(214, 5); bus.Write(211, 10);
        editor.Write("X");
        Assert.Equal(24, bus.Ram[1024 + 5 * 40 + 10]);   // screen code of X
    }
}
