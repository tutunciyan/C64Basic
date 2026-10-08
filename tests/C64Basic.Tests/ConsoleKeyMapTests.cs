using C64Basic.Core.IO;

namespace C64Basic.Tests;

public class ConsoleKeyMapTests
{
    static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0', bool shift = false, bool alt = false, bool control = false) =>
        new(ch, key, shift, alt, control);

    static string? T(ConsoleKeyInfo key) => ConsoleKeyMap.Translate(key, out _, out _);

    [Fact]
    public void PrintableCharactersPassThrough()
    {
        Assert.Equal("A", T(Key(ConsoleKey.A, 'A', shift: true)));
        Assert.Equal("7", T(Key(ConsoleKey.D7, '7')));
        Assert.Equal("£", T(Key(ConsoleKey.NoName, '£')));
        Assert.Equal(" ", T(Key(ConsoleKey.Spacebar, ' ')));
    }

    [Fact]
    public void UnprintableCharactersAreIgnored()
    {
        Assert.Null(T(Key(ConsoleKey.NoName, 'é')));
        Assert.Null(T(Key(ConsoleKey.NoName, '\u0001')));
        Assert.Null(T(Key(ConsoleKey.Tab, '\t')));
    }

    [Theory]
    [InlineData(ConsoleKey.Enter, "\r")]
    [InlineData(ConsoleKey.Backspace, "\u0014")]
    [InlineData(ConsoleKey.Delete, "\u0014")]
    [InlineData(ConsoleKey.Insert, "\u0094")]
    [InlineData(ConsoleKey.Home, "\u0013")]
    [InlineData(ConsoleKey.DownArrow, "\u0011")]
    [InlineData(ConsoleKey.UpArrow, "\u0091")]
    [InlineData(ConsoleKey.RightArrow, "\u001d")]
    [InlineData(ConsoleKey.LeftArrow, "\u009d")]
    public void EditingKeysTypeControlCodes(ConsoleKey key, string expected) =>
        Assert.Equal(expected, T(Key(key)));

    [Fact]
    public void ShiftHomeClearsTheScreen() => Assert.Equal("\u0093", T(Key(ConsoleKey.Home, shift: true)));

    [Fact]
    public void FunctionKeysGiveTheC64Codes()
    {
        var codes = new[] { 133, 137, 134, 138, 135, 139, 136, 140 };
        for (int i = 0; i < 8; i++)
            Assert.Equal(((char)codes[i]).ToString(), T(Key(ConsoleKey.F1 + i)));
    }

    [Fact]
    public void ControlAndAltDigitsPickColours()
    {
        Assert.Equal("\u0090", T(Key(ConsoleKey.D1, '1', control: true)));      // black
        Assert.Equal("\u009e", T(Key(ConsoleKey.D8, '8', control: true)));      // yellow
        Assert.Equal("\u0081", T(Key(ConsoleKey.D1, '1', alt: true)));          // orange
        Assert.Equal("\u009b", T(Key(ConsoleKey.D8, '8', alt: true)));          // light grey
        Assert.Equal("\u0012", T(Key(ConsoleKey.D9, '9', control: true)));      // reverse on
        Assert.Equal("\u0092", T(Key(ConsoleKey.D0, '0', control: true)));      // reverse off
    }

    [Fact]
    public void EscapeIsRunStop()
    {
        Assert.Null(ConsoleKeyMap.Translate(Key(ConsoleKey.Escape, '\u001b'), out bool quit, out bool stop));
        Assert.True(stop);
        Assert.False(quit);
    }

    [Fact]
    public void ControlDQuits()
    {
        Assert.Null(ConsoleKeyMap.Translate(Key(ConsoleKey.D, '\u0004', control: true), out bool quit, out bool stop));
        Assert.True(quit);
        Assert.False(stop);
        // some terminals only deliver the end-of-transmission character
        ConsoleKeyMap.Translate(Key(ConsoleKey.NoName, '\u0004'), out quit, out _);
        Assert.True(quit);
    }

    [Fact]
    public void OrdinaryKeysDoNotQuitOrStop()
    {
        ConsoleKeyMap.Translate(Key(ConsoleKey.A, 'a'), out bool quit, out bool stop);
        Assert.False(quit);
        Assert.False(stop);
    }
}
