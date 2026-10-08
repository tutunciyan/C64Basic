namespace C64Basic.Core.IO;

/// <summary>What a terminal key press types on the C64: PETSCII control codes for the editing and function keys, colour codes for Ctrl/Alt+digit.</summary>
public static class ConsoleKeyMap
{
    /// <param name="quit">Ctrl+D: end of input.</param>
    /// <param name="stop">Esc: RUN/STOP.</param>
    public static string? Translate(ConsoleKeyInfo key, out bool quit, out bool stop)
    {
        quit = stop = false;
        bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        bool alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;

        if ((control && key.Key == ConsoleKey.D) || key.KeyChar == '') { quit = true; return null; }   // end of input
        if (key.Key == ConsoleKey.Escape) { stop = true; return null; }                  // RUN/STOP

        const string controlColours = "\u0090\u0005\u001c\u009f\u009c\u001e\u001f\u009e";
        const string commodoreColours = "\u0081\u0095\u0096\u0097\u0098\u0099\u009a\u009b";
        if ((control || alt) && key.Key is >= ConsoleKey.D1 and <= ConsoleKey.D8)
            return ((control ? controlColours : commodoreColours)[key.Key - ConsoleKey.D1]).ToString();
        if (control && key.Key == ConsoleKey.D9) return "\u0012";
        if (control && key.Key == ConsoleKey.D0) return "\u0092";

        return key.Key switch
        {
            ConsoleKey.Enter => "\r",
            ConsoleKey.Backspace or ConsoleKey.Delete => "\u0014",
            ConsoleKey.Insert => "\u0094",
            ConsoleKey.Home => shift ? "\u0093" : "\u0013",
            ConsoleKey.DownArrow => "\u0011",
            ConsoleKey.UpArrow => "\u0091",
            ConsoleKey.RightArrow => "\u001d",
            ConsoleKey.LeftArrow => "\u009d",
            ConsoleKey.F1 => "\u0085", ConsoleKey.F2 => "\u0089", ConsoleKey.F3 => "\u0086", ConsoleKey.F4 => "\u008a",
            ConsoleKey.F5 => "\u0087", ConsoleKey.F6 => "\u008b", ConsoleKey.F7 => "\u0088", ConsoleKey.F8 => "\u008c",
            _ => key.KeyChar >= ' ' && key.KeyChar < '\u007f' || key.KeyChar == '£' ? key.KeyChar.ToString() : null,
        };
    }

}
