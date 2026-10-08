using System.Collections.Concurrent;
using C64Basic.Core.Machine;

namespace C64Basic.Core.IO;

/// <summary>
/// A console that draws through the C64's own memory: output goes to the <see cref="ScreenEditor"/> (so the VIC-II
/// renders it) and input is a keyboard buffer the host fills with <see cref="Type"/> and the key matrix methods.
/// A host with a window uses this and only has to translate its key events and present
/// <see cref="Vic2.Render"/>. Typing, the cursor and RETURN behave like the real screen editor, including
/// moving the cursor onto an old line and pressing RETURN to re-enter it.
/// </summary>
public sealed class ScreenConsole : IConsoleDevice, IInputDevice
{
    /// <summary>The KERNAL keyboard buffer holds ten characters; further keystrokes are dropped.</summary>
    const int KeyBufferSize = 10;

    readonly object _screenLock = new();
    readonly ConcurrentQueue<char> _keys = new();
    readonly ManualResetEventSlim _keyArrived = new(false);
    readonly bool[] _matrix = new bool[64];
    /// <summary>Joystick state per input source (keyboard, each gamepad) and port; a port reads as the OR of its sources.</summary>
    public const int JoystickSources = 8;

    readonly byte[,] _joystick = new byte[JoystickSources, 3];
    ScreenEditor? _editor;
    volatile bool _waiting, _closed;

    public bool BreakRequested { get; set; }

    /// <summary>True while the interpreter waits for the user to type a line: the cursor blinks only then.</summary>
    public bool CursorVisible => _waiting;

    public int CursorRow => _editor?.Row ?? 0;
    public int CursorColumn => _editor?.Column ?? 0;

    Bus? _screenBus;

    public void Attach(Bus bus)
    {
        _screenBus = bus;
        _editor = new ScreenEditor(bus);
        bus.Input = this;
    }

    // ---------- IConsoleDevice ----------
    public void Write(string text)
    {
        if (_editor == null) return;
        lock (_screenLock) _editor.Write(text);
    }

    public string? ReadLine()
    {
        if (_editor == null) return null;
        int startRow, startColumn, scrolls;
        lock (_screenLock)
        {
            startRow = _editor.Row;
            startColumn = _editor.Column;
            scrolls = _editor.ScrollCount;
        }

        _waiting = true;
        try
        {
            while (true)
            {
                if (_closed) return null;
                if (!_keys.TryDequeue(out char key))
                {
                    _keyArrived.Reset();
                    if (_keys.IsEmpty) _keyArrived.Wait(20);
                    continue;
                }

                lock (_screenLock)
                {
                    if (key == '\r')
                    {
                        // text that scrolled up since the prompt moved with it
                        int row = startRow - (_editor.ScrollCount - scrolls);
                        return _editor.ReadLine(row, startColumn);
                    }
                    _editor.Write(key.ToString());
                }
            }
        }
        finally { _waiting = false; }
    }

    public string GetKey() => _keys.TryDequeue(out char key) ? key.ToString() : "";

    /// <summary>Shift+Commodore: switches between the upper-case/graphics and the lower-case character sets.</summary>
    public void ToggleCharacterSet()
    {
        if (_editor == null) return;
        lock (_screenLock) _editor.LowerCase = !_editor.LowerCase;
    }

    // ---------- keyboard buffer (host side) ----------
    /// <summary>A typed character: a printable character or a control code such as CHR$(13) or a cursor key.</summary>
    public void Type(char key)
    {
        if (_keys.Count >= KeyBufferSize) return;
        _keys.Enqueue(key);
        _keyArrived.Set();
    }

    /// <summary>Feeds text as if typed, without the buffer limit (pasting, loading a dropped file).</summary>
    public void Inject(string text)
    {
        foreach (char c in text) _keys.Enqueue(c == '\n' ? '\r' : c);
        _keyArrived.Set();
    }

    /// <summary>Longest text a paste is allowed to bring in, so a huge clipboard cannot flood the keyboard queue.</summary>
    public const int MaxPaste = 65536;

    /// <summary>
    /// Types clipboard text: line breaks become RETURN, tabs become spaces, and anything the C64 keyboard cannot produce (control
    /// characters, non-ASCII symbols other than the pound sign) is dropped. Returns how many characters were typed.
    /// </summary>
    public int Paste(string text)
    {
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, MaxPaste));
        for (int i = 0; i < text.Length && sb.Length < MaxPaste; i++)
        {
            char c = text[i];
            if (c == '\r') { sb.Append('\r'); if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c == '\n') sb.Append('\r');
            else if (c == '\t') sb.Append(' ');
            else if ((c >= ' ' && c < '\u007f') || c == '£') sb.Append(c);
        }
        Inject(sb.ToString());
        return sb.Length;
    }

    /// <summary>The text on the screen, one line per row with trailing spaces and empty rows at the end removed (for copying).</summary>
    public string ScreenText()
    {
        if (_editor == null) return "";
        var rows = new List<string>();
        lock (_screenLock)
        {
            for (int r = 0; r < ScreenEditor.Rows; r++)
            {
                var sb = new System.Text.StringBuilder(ScreenEditor.Columns);
                for (int c = 0; c < ScreenEditor.Columns; c++)
                    sb.Append(Runtime.Petscii.ScreenGlyph(_screenBus!.Ram[ScreenEditor.ScreenRam + r * ScreenEditor.Columns + c]));
                rows.Add(sb.ToString().TrimEnd(' '));
            }
        }
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        return string.Join("\n", rows);
    }

    /// <summary>Lets a blocked <see cref="ReadLine"/> return null: the window was closed.</summary>
    public void Close()
    {
        _closed = true;
        _keyArrived.Set();
    }

    // ---------- key matrix and joysticks (host side) ----------
    /// <summary>Presses or releases the key with the KERNAL index <c>column * 8 + row</c>.</summary>
    public void SetKey(int index, bool down)
    {
        if (index is >= 0 and < 64) _matrix[index] = down;
    }

    public void ReleaseAllKeys()
    {
        Array.Clear(_matrix);
        Array.Clear(_joystick);
    }

    /// <summary>Sets what one input source (0 = the keyboard, 1-4 = gamepads) holds on a joystick port (1 or 2).</summary>
    public void SetJoystick(int port, byte bits, int source = 0)
    {
        if (port is < 1 or > 2 || source is < 0 or >= JoystickSources) return;
        _joystick[source, port] = JoystickMapping.Normalize(bits);
    }

    public byte KeyColumn(int column)
    {
        int rows = 0;
        for (int row = 0; row < 8; row++) if (_matrix[column * 8 + row]) rows |= 1 << row;
        return (byte)rows;
    }

    public byte Joystick(int port)
    {
        if (port is < 1 or > 2) return 0;
        byte bits = 0;
        for (int source = 0; source < JoystickSources; source++) bits |= _joystick[source, port];
        return JoystickMapping.Normalize(bits);
    }

    volatile bool _restore;

    public bool Restore => _restore;

    /// <summary>Presses or releases the RESTORE key.</summary>
    public void SetRestore(bool down) => _restore = down;
}
