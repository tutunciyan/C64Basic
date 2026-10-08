using C64Basic.Core;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class ScreenConsoleTests
{
    static (ScreenConsole Console, Bus Bus) Make()
    {
        var console = new ScreenConsole();
        var bus = new Bus();
        console.Attach(bus);
        return (console, bus);
    }

    static string Row(Bus bus, int row) =>
        string.Concat(Enumerable.Range(0, 40).Select(c => ScreenEditor.FromScreenCode(bus.Ram[1024 + row * 40 + c]))).TrimEnd();

    static string Screen(Bus bus) => string.Join("\n", Enumerable.Range(0, 25).Select(r => Row(bus, r)));

    [Fact]
    public void ReadLineReturnsWhatWasTypedAndEchoesIt()
    {
        var (console, bus) = Make();
        console.Inject("PRINT 1\r");
        Assert.Equal("PRINT 1", console.ReadLine());
        Assert.Equal("PRINT 1", Row(bus, 0));
        Assert.Equal(1, bus.Ram[214]);
    }

    [Fact]
    public void ThePromptIsNotPartOfTheInput()
    {
        var (console, _) = Make();
        console.Write("? ");
        console.Inject("42\r");
        Assert.Equal("42", console.ReadLine());
    }

    [Fact]
    public void DeleteEditsTheLine()
    {
        var (console, _) = Make();
        console.Inject("PRINX\u0014T\r");
        Assert.Equal("PRINT", console.ReadLine());
    }

    [Fact]
    public void AnOldLineCanBeReenteredWithTheCursor()
    {
        var (console, _) = Make();
        console.Write("10 PRINT 1\nREADY.\n");
        console.Inject("\u0091\u0091\r");
        Assert.Equal("10 PRINT 1", console.ReadLine());
    }

    [Fact]
    public void AnEditedOldLineIsReadAsEdited()
    {
        var (console, _) = Make();
        console.Write("10 PRINT 1\nREADY.\n");
        console.Inject("\u0091\u0091\u001d\u001d\u001d\u001d\u001d\u001d\u001d\u001d\u001d\u001d\u0014" + "2\r");   // replace the 1 with a 2
        Assert.Equal("10 PRINT 2", console.ReadLine());
    }

    [Fact]
    public void GetKeyReturnsTypedCharactersWithoutBlocking()
    {
        var (console, _) = Make();
        Assert.Equal("", console.GetKey());
        console.Type('A');
        console.Type('B');
        Assert.Equal("A", console.GetKey());
        Assert.Equal("B", console.GetKey());
        Assert.Equal("", console.GetKey());
    }

    [Fact]
    public void TheKeyBufferHoldsTenCharacters()
    {
        var (console, _) = Make();
        for (int i = 0; i < 15; i++) console.Type((char)('A' + i));
        string all = "";
        for (string k; (k = console.GetKey()) != ""; ) all += k;
        Assert.Equal("ABCDEFGHIJ", all);
    }

    [Fact]
    public void InjectedTextIsNotLimited()
    {
        var (console, _) = Make();
        console.Inject(new string('X', 30) + "\r");
        Assert.Equal(new string('X', 30), console.ReadLine());
    }

    [Fact]
    public void TextScrollingUpDuringInputKeepsThePrompt()
    {
        var (console, bus) = Make();
        console.Write(string.Concat(Enumerable.Repeat("\n", 24)));   // cursor on the last row
        console.Write("? ");
        console.Inject(new string('A', 60) + "\r");                    // wraps and scrolls
        Assert.Equal(new string('A', 60), console.ReadLine());
        Assert.NotNull(bus);
    }

    [Fact]
    public void CloseUnblocksAWaitingReader()
    {
        var (console, _) = Make();
        new Thread(() => { Thread.Sleep(50); console.Close(); }) { IsBackground = true }.Start();
        Assert.Null(console.ReadLine());
    }

    [Fact]
    public void CursorShowsOnlyWhileWaitingForInput()
    {
        var (console, _) = Make();
        Assert.False(console.CursorVisible);
        string? line = null;
        var reader = new Thread(() => line = console.ReadLine()) { IsBackground = true };   // not the pool: it can be busy on CI
        reader.Start();
        Assert.True(SpinWait.SpinUntil(() => console.CursorVisible, 10000));
        console.Inject("\r");
        Assert.True(reader.Join(10000));
        Assert.Equal("", line);
        Assert.False(console.CursorVisible);
    }

    [Fact]
    public void KeyMatrixAndJoysticksFeedTheCia()
    {
        var (console, bus) = Make();
        console.SetKey(9, true);                  // W: column 1, row 1
        Assert.Equal(0b10, console.KeyColumn(1));
        console.SetKey(15, true);                 // left shift: column 1, row 7
        Assert.Equal(0b1000_0010, console.KeyColumn(1));
        console.SetKey(9, false);
        Assert.Equal(0b1000_0000, console.KeyColumn(1));
        Assert.Equal(15, bus.Read(197) == 64 ? 15 : 0);   // shift alone is not a key
        Assert.Equal(1, bus.Read(653));

        console.SetJoystick(2, 0b10001);
        Assert.Equal(0b10001, console.Joystick(2));
        console.ReleaseAllKeys();
        Assert.Equal(0, console.KeyColumn(1));
        Assert.Equal(0, console.Joystick(2));
    }

    [Fact]
    public void AnInterpreterDrawsOnTheScreenThroughTheReplLoop()
    {
        var console = new ScreenConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        var repl = new Repl(interp, console);
        var thread = new Thread(() => repl.Run()) { IsBackground = true };
        thread.Start();

        console.Inject("PRINT 6*7\r");
        var bus = interp.Bus;
        Assert.True(SpinWait.SpinUntil(() => Screen(bus).Contains(" 42"), 10000), Screen(bus));
        Assert.Contains("READY.", Screen(bus));
        Assert.Contains("COMMODORE 64 BASIC V2", Screen(bus));

        console.Inject("POKE 53280,2\r");
        Assert.True(SpinWait.SpinUntil(() => bus.Vic.Border == 2, 10000));

        console.Close();
        Assert.True(thread.Join(10000));
    }

    [Fact]
    public void ProgramsCanReadTheScreenWithPeek()
    {
        var console = new ScreenConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine("PRINT \"A\"");
        interp.ProcessLine("PRINT PEEK(1024)");
        Assert.Contains("\n 1", Screen(interp.Bus));   // screen code of A
    }
}
