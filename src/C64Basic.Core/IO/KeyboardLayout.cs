namespace C64Basic.Core.IO;

/// <summary>
/// Where a typed character sits on the C64 keyboard: the keys of the matrix (index = column * 8 + row, as in
/// <see cref="ScreenConsole.SetKey"/>) that make it, including Shift where the character needs it. Used by hosts that only
/// learn about typed characters (a terminal), to also drive the key matrix machine code scans.
/// </summary>
public static class KeyboardLayout
{
    public const int LeftShift = 15;

    static readonly Dictionary<char, int[]> Keys = Build();

    static Dictionary<char, int[]> Build()
    {
        var m = new Dictionary<char, int[]>();
        void Add(char c, params int[] keys) => m[c] = keys;

        // letters: plain for lower case, with Shift for capitals (Shift+letter is a graphics symbol on a C64)
        int[] letters = { 10, 28, 20, 18, 14, 21, 26, 29, 33, 34, 37, 42, 36, 39, 38, 41, 62, 17, 13, 22, 30, 31, 9, 23, 25, 12 };
        for (int i = 0; i < 26; i++)
        {
            Add((char)('a' + i), letters[i]);
            Add((char)('A' + i), LeftShift, letters[i]);
        }

        // digits and the symbols above them
        int[] digits = { 35, 56, 59, 8, 11, 16, 19, 24, 27, 32 };       // 0 1 2 ... 9
        for (int i = 0; i < 10; i++) Add((char)('0' + i), digits[i]);
        Add('!', LeftShift, 56); Add('"', LeftShift, 59); Add('#', LeftShift, 8); Add('$', LeftShift, 11);
        Add('%', LeftShift, 16); Add('&', LeftShift, 19); Add('\'', LeftShift, 24); Add('(', LeftShift, 27);
        Add(')', LeftShift, 32);

        Add(' ', 60);
        Add('+', 40); Add('-', 43); Add('*', 49); Add('/', 55); Add('=', 53); Add('@', 46); Add('£', 48);
        Add(':', 45); Add(';', 50); Add(',', 47); Add('.', 44);
        Add('[', LeftShift, 45); Add(']', LeftShift, 50);
        Add('<', LeftShift, 47); Add('>', LeftShift, 44); Add('?', LeftShift, 55);
        Add('^', 54); Add('_', 57);

        // control codes the editing keys type
        Add('\r', 1);
        Add('\u0014', 0);                       // DEL
        Add('\u0094', LeftShift, 0);            // INST
        Add('\u0013', 51);                      // HOME
        Add('\u0093', LeftShift, 51);           // CLR
        Add('\u0011', 7);                       // cursor down
        Add('\u0091', LeftShift, 7);            // cursor up
        Add('\u001d', 2);                       // cursor right
        Add('\u009d', LeftShift, 2);            // cursor left
        Add('\u0085', 4); Add('\u0089', LeftShift, 4);       // F1, F2
        Add('\u0086', 5); Add('\u008a', LeftShift, 5);       // F3, F4
        Add('\u0087', 6); Add('\u008b', LeftShift, 6);       // F5, F6
        Add('\u0088', 3); Add('\u008c', LeftShift, 3);       // F7, F8
        return m;
    }

    /// <summary>The keys to hold for a character, or null if the keyboard has no way to type it (colour codes, for one).</summary>
    public static int[]? KeysFor(char c) => Keys.TryGetValue(c, out var keys) ? keys : null;
}
