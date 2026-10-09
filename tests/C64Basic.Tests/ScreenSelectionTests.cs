using C64Basic.Core.IO;

namespace C64Basic.Tests;

public class ScreenSelectionTests
{
    static string[] Rows(params string[] text) => Enumerable.Range(0, 25).Select(i => i < text.Length ? text[i] : "").ToArray();

    [Fact]
    public void ASelectionOnOneLineIsThatStretch()
    {
        var rows = Rows("READY.", "10 PRINT \"HELLO\"");
        var s = new ScreenSelection(3, 1);
        s.Extend(10, 1);
        Assert.True(s.IsRange);
        Assert.Equal("PRINT \"H", s.Extract(rows));
    }

    [Fact]
    public void ADragBackwardsSelectsTheSameText()
    {
        var rows = Rows("ABCDEFGH");
        var s = new ScreenSelection(5, 0);
        s.Extend(2, 0);
        Assert.Equal("CDEF", s.Extract(rows));
    }

    [Fact]
    public void ALongerSelectionTakesTheRestOfTheFirstLineWholeLinesAndThePartOfTheLast()
    {
        var rows = Rows("first line", "second   ", "third line here");
        var s = new ScreenSelection(6, 0);
        s.Extend(4, 2);
        Assert.Equal("line\nsecond\nthird", s.Extract(rows));
    }

    [Fact]
    public void TrailingSpacesAreCutAndAnEmptyLineStaysAsOne()
    {
        var rows = Rows("A    ", "", "B");
        var s = new ScreenSelection(0, 0);
        s.Extend(39, 2);
        Assert.Equal("A\n\nB", s.Extract(rows));
    }

    [Fact]
    public void ThePointsAreClampedToTheScreen()
    {
        var s = new ScreenSelection(-5, 99);
        Assert.Equal(0, s.AnchorColumn);
        Assert.Equal(24, s.AnchorRow);
        s.Extend(500, -3);
        Assert.Equal(39, s.Column);
        Assert.Equal(0, s.Row);
        Assert.True(s.Contains(0, 12));
        Assert.True(s.Contains(39, 0));
    }

    [Fact]
    public void ContainsFollowsTheLines()
    {
        var s = new ScreenSelection(30, 2);
        s.Extend(5, 4);
        Assert.False(s.Contains(29, 2));
        Assert.True(s.Contains(30, 2));
        Assert.True(s.Contains(0, 3));
        Assert.True(s.Contains(39, 3));
        Assert.True(s.Contains(5, 4));
        Assert.False(s.Contains(6, 4));
    }

    [Fact]
    public void AFramePointIsMappedToACell()
    {
        Assert.Equal((0, 0), ScreenSelection.CellAt(32, 36));
        Assert.Equal((39, 24), ScreenSelection.CellAt(32 + 319, 36 + 199));
        Assert.Equal((1, 0), ScreenSelection.CellAt(40, 43));
        Assert.Null(ScreenSelection.CellAt(10, 100));                     // in the border
        Assert.Null(ScreenSelection.CellAt(100, 20));
        Assert.Equal((0, 0), ScreenSelection.CellAt(0, 0, clamp: true));      // dragging out of the text area sticks to its edge
        Assert.Equal((39, 24), ScreenSelection.CellAt(383, 271, clamp: true));
    }
}
