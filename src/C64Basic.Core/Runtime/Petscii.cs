namespace C64Basic.Core.Runtime;

/// <summary>
/// PETSCII graphics codes. CHR$ returns codes 96-127 and 160-255 as characters in a private-use block
/// (U+EE00 + code) so they stay distinct from typed ASCII text such as "m"; ASC maps them back.
/// A front end turns them into glyphs with <see cref="Glyph"/>.
/// </summary>
public static class Petscii
{
    public const int Base = 0xEE00;

    // Codes 96-127 and 192-223 (the same shapes), indexed from 0.
    const string Upper = "─♠│───││┐╮╰╯▗╲╱▘▝●─♥│╭╳○♣│♦┼▒│π◥";

    // Codes 160-190 and 224-254, indexed from 0.
    const string Lower = " ▌▄▔▁▏▒▕◤◥├▗└┐▂┌┴┬┤▎▍▕▔▔▃✓▖▝┘▘▚";

    static bool IsGraphic(int n) => (n >= 96 && n <= 127) || n >= 160;

    public static char ToChar(int code) => IsGraphic(code) ? (char)(Base + code) : (char)code;

    /// <summary>Code for ASC: typed lower-case letters are the upper-case PETSCII letters, as on a C64 in its default mode.</summary>
    public static int ToCode(char c) =>
        c >= Base && c < Base + 256 ? c - Base : c >= 'a' && c <= 'z' ? c - 32 : c;

    /// <summary>
    /// What Shift+letter types on a C64: PETSCII 193-218, a graphics symbol in the first character set and the capital
    /// letter in the lower-case set. Other characters come back unchanged.
    /// </summary>
    public static char ShiftedLetter(char c) =>
        c is >= 'A' and <= 'Z' ? ToChar(193 + (c - 'A')) : c is >= 'a' and <= 'z' ? ToChar(193 + (c - 'a')) : c;

    public static bool IsGlyph(char c) => c >= Base && c < Base + 256;

    /// <summary>The Unicode character that best draws a PETSCII graphics character.</summary>
    public static char Glyph(char c)
    {
        int code = c - Base;
        if (code >= 192 && code <= 223) code -= 96;
        if (code >= 96 && code <= 127) return Upper[code - 96];
        if (code >= 224 && code <= 254) code -= 64;
        if (code >= 160 && code <= 190) return Lower[Math.Min(code - 160, Lower.Length - 1)];
        return code == 255 ? 'π' : '#';
    }

    /// <summary>The Unicode character for a screen RAM code (what POKE 1024+n draws). Codes 128+ are the reversed shapes.</summary>
    public static char ScreenGlyph(int code)
    {
        int c = code & 127;
        if (c == 0) return '@';
        if (c <= 26) return (char)('A' + c - 1);
        if (c <= 31) return "[£]↑←"[c - 27];
        if (c < 64) return (char)c;
        if (c < 96) return Upper[c - 64];
        return Lower[Math.Min(c - 96, Lower.Length - 1)];
    }
}
