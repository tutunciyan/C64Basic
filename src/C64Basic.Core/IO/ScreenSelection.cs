namespace C64Basic.Core.IO;

/// <summary>
/// A stretch of text on the 40x25 screen picked with the mouse, the way a terminal does it: from the cell where the drag began to the
/// cell where it is now, along the lines (the first line from its start, the last up to its end, whole lines between).
/// </summary>
public sealed class ScreenSelection
{
    public const int Columns = 40, Rows = 25;

    public int AnchorColumn { get; }
    public int AnchorRow { get; }
    public int Column { get; private set; }
    public int Row { get; private set; }

    public ScreenSelection(int column, int row)
    {
        AnchorColumn = Clamp(column, Columns); AnchorRow = Clamp(row, Rows);
        Column = AnchorColumn; Row = AnchorRow;
    }

    static int Clamp(int value, int count) => Math.Clamp(value, 0, count - 1);

    /// <summary>Moves the free end of the selection (clamped to the screen).</summary>
    public void Extend(int column, int row)
    {
        Column = Clamp(column, Columns);
        Row = Clamp(row, Rows);
    }

    int From => Math.Min(AnchorRow * Columns + AnchorColumn, Row * Columns + Column);
    int To => Math.Max(AnchorRow * Columns + AnchorColumn, Row * Columns + Column);

    /// <summary>True when the selection is more than the one cell it began on.</summary>
    public bool IsRange => From != To;

    public bool Contains(int column, int row)
    {
        int cell = row * Columns + column;
        return cell >= From && cell <= To;
    }

    /// <summary>The selected text of the screen's rows (each 40 characters, or fewer when trailing spaces were cut): lines joined by newlines, trailing spaces of each line removed.</summary>
    public string Extract(IReadOnlyList<string> rows)
    {
        var lines = new List<string>();
        for (int r = From / Columns; r <= To / Columns && r < rows.Count; r++)
        {
            int first = r == From / Columns ? From % Columns : 0;
            int last = r == To / Columns ? To % Columns : Columns - 1;
            string row = rows[r].PadRight(Columns);
            lines.Add(row.Substring(first, last - first + 1).TrimEnd(' '));
        }
        return string.Join("\n", lines);
    }

    /// <summary>The cell under a point of the 384x272 frame (the 320x200 text area sits 32 and 36 pixels in), or null outside the text area.</summary>
    public static (int Column, int Row)? CellAt(int frameX, int frameY, bool clamp = false)
    {
        int x = frameX - 32, y = frameY - 36;
        int column = x >= 0 ? x / 8 : -1, row = y >= 0 ? y / 8 : -1;
        if (clamp) return (Clamp(x < 0 ? 0 : column, Columns), Clamp(y < 0 ? 0 : row, Rows));
        return column is >= 0 and < Columns && row is >= 0 and < Rows ? (column, row) : null;
    }
}
