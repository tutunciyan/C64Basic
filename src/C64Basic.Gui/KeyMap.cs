using Silk.NET.SDL;

namespace C64Basic.Gui;

/// <summary>Where host keys land on the C64 keyboard matrix (index = column * 8 + row) and what they type at the prompt.</summary>
static class KeyMap
{
    const int LeftShift = 15, Del = 0, CursorRight = 2, CursorDown = 7, Home = 51, Return = 1, Stop = 63;

    /// <summary>Matrix keys held while a host key is down. Several entries mean a shifted key (cursor up is shift + down).</summary>
    public static readonly Dictionary<Scancode, int[]> Matrix = Build();

    static Dictionary<Scancode, int[]> Build()
    {
        var m = new Dictionary<Scancode, int[]>();
        void Add(Scancode s, params int[] keys) => m[s] = keys;

        // letters
        (Scancode, int)[] letters =
        {
            (Scancode.ScancodeA, 10), (Scancode.ScancodeB, 28), (Scancode.ScancodeC, 20), (Scancode.ScancodeD, 18),
            (Scancode.ScancodeE, 14), (Scancode.ScancodeF, 21), (Scancode.ScancodeG, 26), (Scancode.ScancodeH, 29),
            (Scancode.ScancodeI, 33), (Scancode.ScancodeJ, 34), (Scancode.ScancodeK, 37), (Scancode.ScancodeL, 42),
            (Scancode.ScancodeM, 36), (Scancode.ScancodeN, 39), (Scancode.ScancodeO, 38), (Scancode.ScancodeP, 41),
            (Scancode.ScancodeQ, 62), (Scancode.ScancodeR, 17), (Scancode.ScancodeS, 13), (Scancode.ScancodeT, 22),
            (Scancode.ScancodeU, 30), (Scancode.ScancodeV, 31), (Scancode.ScancodeW, 9), (Scancode.ScancodeX, 23),
            (Scancode.ScancodeY, 25), (Scancode.ScancodeZ, 12),
        };
        foreach (var (s, k) in letters) Add(s, k);

        // digits
        (Scancode, int)[] digits =
        {
            (Scancode.Scancode1, 56), (Scancode.Scancode2, 59), (Scancode.Scancode3, 8), (Scancode.Scancode4, 11),
            (Scancode.Scancode5, 16), (Scancode.Scancode6, 19), (Scancode.Scancode7, 24), (Scancode.Scancode8, 27),
            (Scancode.Scancode9, 32), (Scancode.Scancode0, 35),
        };
        foreach (var (s, k) in digits) Add(s, k);

        Add(Scancode.ScancodeSpace, 60);
        Add(Scancode.ScancodeReturn, Return);
        Add(Scancode.ScancodeKPEnter, Return);
        Add(Scancode.ScancodeBackspace, Del);
        Add(Scancode.ScancodeDelete, Del);
        Add(Scancode.ScancodeInsert, LeftShift, Del);
        Add(Scancode.ScancodeHome, Home);
        Add(Scancode.ScancodeEscape, Stop);
        Add(Scancode.ScancodeGrave, 57);               // left arrow
        Add(Scancode.ScancodePageup, 54);              // up arrow
        Add(Scancode.ScancodeMinus, 43);
        Add(Scancode.ScancodeEquals, 53);
        Add(Scancode.ScancodeKPPlus, 40);
        Add(Scancode.ScancodeKPMinus, 43);
        Add(Scancode.ScancodeKPMultiply, 49);
        Add(Scancode.ScancodeKPDivide, 55);
        Add(Scancode.ScancodeLeftbracket, 46);         // @
        Add(Scancode.ScancodeRightbracket, 49);        // *
        Add(Scancode.ScancodeBackslash, 48);           // pound
        Add(Scancode.ScancodeSemicolon, 45);           // :
        Add(Scancode.ScancodeApostrophe, 50);          // ;
        Add(Scancode.ScancodeComma, 47);
        Add(Scancode.ScancodePeriod, 44);
        Add(Scancode.ScancodeSlash, 55);

        Add(Scancode.ScancodeLshift, LeftShift);
        Add(Scancode.ScancodeRshift, 52);
        Add(Scancode.ScancodeLctrl, 58);
        Add(Scancode.ScancodeRctrl, 58);
        Add(Scancode.ScancodeLalt, 61);                // Commodore key
        Add(Scancode.ScancodeRalt, 61);

        Add(Scancode.ScancodeRight, CursorRight);
        Add(Scancode.ScancodeDown, CursorDown);
        Add(Scancode.ScancodeLeft, LeftShift, CursorRight);
        Add(Scancode.ScancodeUp, LeftShift, CursorDown);

        Add(Scancode.ScancodeF1, 4);
        Add(Scancode.ScancodeF3, 5);
        Add(Scancode.ScancodeF5, 6);
        Add(Scancode.ScancodeF7, 3);
        Add(Scancode.ScancodeF2, LeftShift, 4);
        Add(Scancode.ScancodeF4, LeftShift, 5);
        Add(Scancode.ScancodeF6, LeftShift, 6);
        Add(Scancode.ScancodeF8, LeftShift, 3);
        return m;
    }

    /// <summary>Characters typed at the prompt by keys that produce no text event.</summary>
    public static char? Typed(Scancode key, bool shift) => key switch
    {
        Scancode.ScancodeReturn or Scancode.ScancodeKPEnter => '\r',
        Scancode.ScancodeBackspace => '\u0014',
        Scancode.ScancodeDelete => '\u0014',
        Scancode.ScancodeInsert => '\u0094',
        Scancode.ScancodeHome => shift ? '\u0093' : '\u0013',
        Scancode.ScancodeDown => '\u0011',
        Scancode.ScancodeUp => '\u0091',
        Scancode.ScancodeRight => '\u001d',
        Scancode.ScancodeLeft => '\u009d',
        Scancode.ScancodeF1 => '\u0085',
        Scancode.ScancodeF2 => '\u0089',
        Scancode.ScancodeF3 => '\u0086',
        Scancode.ScancodeF4 => '\u008a',
        Scancode.ScancodeF5 => '\u0087',
        Scancode.ScancodeF6 => '\u008b',
        Scancode.ScancodeF7 => '\u0088',
        Scancode.ScancodeF8 => '\u008c',
        _ => null,
    };

    static readonly char[] ControlColors = { '\u0090', '\u0005', '\u001c', '\u009f', '\u009c', '\u001e', '\u001f', '\u009e' };
    static readonly char[] CommodoreColors = { '\u0081', '\u0095', '\u0096', '\u0097', '\u0098', '\u0099', '\u009a', '\u009b' };

    /// <summary>Ctrl+1..8 and Commodore+1..8 pick the text colour, Ctrl+9 / Ctrl+0 switch reverse video.</summary>
    public static char? Color(Scancode key, bool control, bool commodore)
    {
        int digit = key switch
        {
            Scancode.Scancode1 => 1, Scancode.Scancode2 => 2, Scancode.Scancode3 => 3, Scancode.Scancode4 => 4,
            Scancode.Scancode5 => 5, Scancode.Scancode6 => 6, Scancode.Scancode7 => 7, Scancode.Scancode8 => 8,
            Scancode.Scancode9 => 9, Scancode.Scancode0 => 0, _ => -1,
        };
        if (digit < 0) return null;
        if (control && digit == 9) return '\u0012';
        if (control && digit == 0) return '\u0092';
        if (digit is >= 1 and <= 8)
        {
            if (control) return ControlColors[digit - 1];
            if (commodore) return CommodoreColors[digit - 1];
        }
        return null;
    }

    /// <summary>Numpad joystick for port 2: bit 0 up, 1 down, 2 left, 3 right, 4 fire.</summary>
    public static byte JoystickBit(Scancode key) => key switch
    {
        Scancode.ScancodeKP8 => 1,
        Scancode.ScancodeKP2 or Scancode.ScancodeKP5 => 2,
        Scancode.ScancodeKP4 => 4,
        Scancode.ScancodeKP6 => 8,
        Scancode.ScancodeKP0 => 16,
        _ => 0,
    };
}
