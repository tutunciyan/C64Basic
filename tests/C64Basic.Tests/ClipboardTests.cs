using C64Basic.Core.IO;
using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class ClipboardTests
{
    static ScreenConsole Make()
    {
        var console = new ScreenConsole();
        console.Attach(new Bus());
        return console;
    }

    static string Typed(ScreenConsole console)
    {
        var sb = new System.Text.StringBuilder();
        for (string k; (k = console.GetKey()) != ""; ) sb.Append(k);
        return sb.ToString();
    }

    [Fact]
    public void LineBreaksBecomeReturn()
    {
        var console = Make();
        Assert.Equal(17, console.Paste("10 PRINT 1\n20 END"));
        Assert.Equal("10 PRINT 1\r20 END", Typed(console));
    }

    [Fact]
    public void AllLineEndingsAreSingleReturns()
    {
        var console = Make();
        console.Paste("A\r\nB\nC\rD");
        // the key buffer limit does not apply to pastes: read it all back through a line reader
        var lines = new List<string?>();
        for (int i = 0; i < 3; i++) lines.Add(console.ReadLine());   // the last line has no break, so only three are complete
        Assert.Equal(new string?[] { "A", "B", "C" }, lines);
    }

    [Fact]
    public void TabsBecomeSpacesAndControlCharactersAreDropped()
    {
        var console = Make();
        console.Paste("A\tB\u0001C\u001bD\u007fE");
        Assert.Equal("A BCDE", Typed(console));
    }

    [Fact]
    public void NonAsciiSymbolsAreDroppedExceptThePound()
    {
        var console = Make();
        console.Paste("5£ é€中");
        Assert.Equal("5£ ", Typed(console));
    }

    [Fact]
    public void LongPastesAreNotLimitedByTheKeyBuffer()
    {
        var console = Make();
        console.Paste(new string('X', 200) + "\n");
        Assert.Equal(new string('X', 200), console.ReadLine());
    }

    [Fact]
    public void HugePastesAreCapped()
    {
        var console = Make();
        int typed = console.Paste(new string('A', ScreenConsole.MaxPaste + 5000));
        Assert.Equal(ScreenConsole.MaxPaste, typed);
    }

    [Fact]
    public void EmptyPastesDoNothing()
    {
        var console = Make();
        Assert.Equal(0, console.Paste(""));
        Assert.Equal("", console.GetKey());
    }

    [Fact]
    public void ScreenTextReturnsTheVisibleLines()
    {
        var console = Make();
        console.Write("HELLO\nWORLD  \n\n\nEND");
        Assert.Equal("HELLO\nWORLD\n\n\nEND", console.ScreenText());
    }

    [Fact]
    public void ScreenTextOfABlankScreenIsEmpty() => Assert.Equal("", Make().ScreenText());

    [Fact]
    public void ScreenTextUsesReadableSymbolsForGraphics()
    {
        var console = Make();
        console.Write("A" + C64Basic.Core.Runtime.Petscii.ToChar(97));
        string text = console.ScreenText();
        Assert.StartsWith("A", text);
        Assert.True(text.Length >= 2);
    }

    [Fact]
    public void APastedProgramRunsLikeTypedLines()
    {
        var console = new ScreenConsole();
        var interp = new C64Basic.Core.Runtime.Interpreter(console, new MemoryFileSystem());
        var repl = new C64Basic.Core.Repl(interp, console);
        var thread = new Thread(() => repl.Run()) { IsBackground = true };
        thread.Start();

        console.Paste("10 FOR I=1 TO 3\r\n20 PRINT I*I\r\n30 NEXT\r\nRUN\r\n");
        var bus = interp.Bus;
        bool ok = SpinWait.SpinUntil(() => console.ScreenText().Contains(" 9"), 10000);
        Assert.True(ok, console.ScreenText());
        Assert.Contains(" 1", console.ScreenText());
        console.Close();
        Assert.True(thread.Join(10000));
        Assert.NotNull(bus);
    }
}
