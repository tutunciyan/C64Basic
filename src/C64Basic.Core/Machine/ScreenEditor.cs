using System.Text;
using C64Basic.Core.Runtime;

namespace C64Basic.Core.Machine;

/// <summary>
/// The KERNAL's screen editor: prints PETSCII text into screen RAM (1024) and colour RAM with the cursor kept in
/// zero page (row 214, column 211, colour 646), so the VIC-II draws what the program printed and PEEK/POKE see it.
/// Lines that wrap are linked, so a typed 80-character BASIC line is read back as one logical line.
/// </summary>
public sealed class ScreenEditor
{
    public const int Columns = 40, Rows = 25, ScreenRam = 1024;
    const int CursorRow = 214, CursorColumn = 211, TextColor = 646, ReverseFlag = 199, QuoteFlag = 212;

    static readonly Dictionary<char, int> ColorCodes = new()
    {
        ['\u0090'] = 0, ['\u0005'] = 1, ['\u001c'] = 2, ['\u009f'] = 3, ['\u009c'] = 4, ['\u001e'] = 5,
        ['\u001f'] = 6, ['\u009e'] = 7, ['\u0081'] = 8, ['\u0095'] = 9, ['\u0096'] = 10, ['\u0097'] = 11,
        ['\u0098'] = 12, ['\u0099'] = 13, ['\u009a'] = 14, ['\u009b'] = 15,
    };

    readonly Bus _bus;
    readonly bool[] _continues = new bool[Rows]; // this row continues the logical line of the row above

    /// <summary>How many times the screen has scrolled; a reader that remembers a row uses it to follow the text up.</summary>
    public int ScrollCount { get; private set; }

    public ScreenEditor(Bus bus)
    {
        _bus = bus;
        Clear();
    }

    public int Row { get => Math.Min((int)_bus.Ram[CursorRow], Rows - 1); set => _bus.Ram[CursorRow] = (byte)value; }
    public int Column { get => Math.Min((int)_bus.Ram[CursorColumn], Columns - 1); set => _bus.Ram[CursorColumn] = (byte)value; }
    int Color => _bus.Ram[TextColor] & 15;
    /// <summary>
    /// Quote mode: after an opening quote, control codes are shown as reverse-video symbols instead of being obeyed,
    /// so they can be typed into strings. It ends at RETURN or at the closing quote.
    /// </summary>
    public bool Quote { get => _bus.Ram[QuoteFlag] != 0; private set => _bus.Ram[QuoteFlag] = (byte)(value ? 1 : 0); }

    /// <summary>True while the lower-case character set is selected ($D018 bit 1).</summary>
    public bool LowerCase
    {
        get => (_bus.Vic.Read(0xD018) & 2) != 0;
        set => _bus.Vic.Write(0xD018, (byte)((_bus.Vic.Read(0xD018) & ~2) | (value ? 2 : 0)));
    }

    bool Reverse { get => _bus.Ram[ReverseFlag] != 0; set => _bus.Ram[ReverseFlag] = (byte)(value ? 1 : 0); }

    // ---------- conversions ----------
    /// <summary>The screen code a character is drawn with.</summary>
    public static int ToScreenCode(char c)
    {
        if (Petscii.IsGlyph(c))
        {
            int p = c - Petscii.Base;
            if (p <= 127) return p - 32;
            if (p <= 191) return p - 64;
            if (p <= 254) return p - 128;
            return 94;
        }
        if (c >= 'a' && c <= 'z') return c - 'a' + 1;
        if (c >= 'A' && c <= 'Z') return c - 'A' + 1;
        return c switch
        {
            '@' => 0, '[' => 27, '£' => 28, ']' => 29, '^' => 30, '_' => 31, '|' => 93,
            >= ' ' and <= '?' => c,
            _ => 63,
        };
    }

    /// <summary>The character a screen code reads back as when a line is read from the screen.</summary>
    public static char FromScreenCode(int code)
    {
        int c = code & 0x7F;
        int petscii = c < 32 ? c + 64 : c < 64 ? c : c < 96 ? c + 32 : c + 64;
        return petscii == 92 ? '£' : Petscii.ToChar(petscii);
    }

    // ---------- printing ----------
    public void Write(string text)
    {
        foreach (char c in text)
        {
            if (Quote && IsQuotedControl(c)) { Put(QuotedScreenCode(c)); continue; }
            switch (c)
            {
                case '\n': case '\r': case '\u008d': Reverse = false; Quote = false; NewLine(); break;
                case '\u000e': LowerCase = true; break;
                case '\u008e': LowerCase = false; break;
                case '\u0093': Clear(); break;
                case '\u0013': Row = 0; Column = 0; break;
                case '\u0011': CursorDown(); break;
                case '\u0091': if (Row > 0) Row--; break;
                case '\u001d': CursorRight(); break;
                case '\u009d': CursorLeft(); break;
                case '\u0012': Reverse = true; break;
                case '\u0092': Reverse = false; break;
                case '\u0014': Delete(); break;
                case '\u0094': Insert(); break;
                default:
                    if (ColorCodes.TryGetValue(c, out int color)) _bus.Ram[TextColor] = (byte)color;
                    else if (c >= ' ' && !(c >= '\u0080' && c <= '\u009f'))
                    {
                        Put(ToScreenCode(c) + (Reverse ? 128 : 0));
                        if (c == '"') Quote = !Quote;
                    }
                    break;
            }
        }
    }

    /// <summary>Control codes a quote shows as symbols; RETURN, DEL and INST keep working inside quotes.</summary>
    static bool IsQuotedControl(char c) =>
        (c < ' ' || (c >= '\u0080' && c <= '\u009f')) && c is not ('\r' or '\n' or '\u008d' or '\u0014' or '\u0094');

    /// <summary>The reverse-video screen code a control code is drawn with inside quotes.</summary>
    static int QuotedScreenCode(char c) => c < ' ' ? c + 128 : c + 64;

    void Put(int code)
    {
        int cell = Row * Columns + Column;
        _bus.Write(ScreenRam + cell, (byte)code);
        _bus.Color.Data[cell] = (byte)Color;
        if (Column < Columns - 1) { Column++; return; }
        Column = 0;
        Advance(linked: true);
    }

    void NewLine()
    {
        Column = 0;
        Advance(linked: false);
    }

    /// <summary>Moves to the next row, scrolling at the bottom.</summary>
    void Advance(bool linked)
    {
        if (Row < Rows - 1) Row++; else Scroll();
        _continues[Row] = linked;
    }

    void CursorDown()
    {
        if (Row < Rows - 1) Row++; else Scroll();
    }

    void CursorRight()
    {
        if (Column < Columns - 1) { Column++; return; }
        if (Row < Rows - 1) { Column = 0; Row++; _continues[Row] = true; }
    }

    void CursorLeft()
    {
        if (Column > 0) { Column--; return; }
        if (Row > 0) { Column = Columns - 1; Row--; }
    }

    /// <summary>DEL: removes the character left of the cursor and pulls the rest of the row in.</summary>
    void Delete()
    {
        if (Column == 0 && Row == 0) return;
        CursorLeft();
        int start = Row * Columns + Column, end = Row * Columns + Columns - 1;
        for (int i = start; i < end; i++)
        {
            _bus.Ram[ScreenRam + i] = _bus.Ram[ScreenRam + i + 1];
            _bus.Color.Data[i] = _bus.Color.Data[i + 1];
        }
        _bus.Ram[ScreenRam + end] = 32;
        _bus.Color.Data[end] = (byte)Color;
    }

    /// <summary>INST: opens a gap at the cursor by pushing the rest of the row right.</summary>
    void Insert()
    {
        int start = Row * Columns + Column, end = Row * Columns + Columns - 1;
        for (int i = end; i > start; i--)
        {
            _bus.Ram[ScreenRam + i] = _bus.Ram[ScreenRam + i - 1];
            _bus.Color.Data[i] = _bus.Color.Data[i - 1];
        }
        _bus.Ram[ScreenRam + start] = 32;
        _bus.Color.Data[start] = (byte)Color;
    }

    void Scroll()
    {
        ScrollCount++;
        Array.Copy(_bus.Ram, ScreenRam + Columns, _bus.Ram, ScreenRam, (Rows - 1) * Columns);
        Array.Copy(_bus.Color.Data, Columns, _bus.Color.Data, 0, (Rows - 1) * Columns);
        Array.Fill(_bus.Ram, (byte)32, ScreenRam + (Rows - 1) * Columns, Columns);
        Array.Fill(_bus.Color.Data, (byte)Color, (Rows - 1) * Columns, Columns);
        Array.Copy(_continues, 1, _continues, 0, Rows - 1);
        _continues[Rows - 1] = false;
    }

    public void Clear()
    {
        Array.Fill(_bus.Ram, (byte)32, ScreenRam, Rows * Columns);
        Array.Fill(_bus.Color.Data, (byte)Color, 0, Rows * Columns);
        Array.Clear(_continues);
        Quote = false;
        Row = 0;
        Column = 0;
    }

    // ---------- reading a line from the screen ----------
    /// <summary>
    /// What the user has on screen when they press RETURN: the logical line the cursor is on, starting at
    /// (<paramref name="startRow"/>, <paramref name="startColumn"/>) if that is inside it (so a prompt is skipped).
    /// The cursor then moves to the start of the next line.
    /// </summary>
    public string ReadLine(int startRow, int startColumn)
    {
        int first = Row;
        while (first > 0 && _continues[first]) first--;
        int last = first;
        while (last < Rows - 1 && _continues[last + 1]) last++;

        int from = first * Columns;
        if (startRow >= first && startRow <= last) from = startRow * Columns + startColumn;

        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = from; i < (last + 1) * Columns; i++)
        {
            int code = _bus.Ram[ScreenRam + i];
            // inside quotes the reverse symbols stand for the control codes that were typed
            if (quoted && code is >= 128 and <= 159) sb.Append((char)(code - 128));
            else if (quoted && code is >= 192 and <= 223) sb.Append((char)(code - 64));
            else
            {
                if ((code & 0x7F) == '"') quoted = !quoted;
                sb.Append(FromScreenCode(code));
            }
        }
        string line = sb.ToString().TrimEnd(' ');

        Row = last;
        Column = 0;
        Reverse = false;
        Quote = false;
        Advance(linked: false);
        return line;
    }
}
