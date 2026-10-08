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
    readonly byte[] _joystick = new byte[3];
    ScreenEditor? _editor;
    volatile bool _waiting, _closed;

    public bool BreakRequested { get; set; }

    /// <summary>True while the interpreter waits for the user to type a line: the cursor blinks only then.</summary>
    public bool CursorVisible => _waiting;

    public int CursorRow => _editor?.Row ?? 0;
    public int CursorColumn => _editor?.Column ?? 0;

    public void Attach(Bus bus)
    {
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

    public void SetJoystick(int port, byte bits) => _joystick[port] = bits;

    public byte KeyColumn(int column)
    {
        int rows = 0;
        for (int row = 0; row < 8; row++) if (_matrix[column * 8 + row]) rows |= 1 << row;
        return (byte)rows;
    }

    public byte Joystick(int port) => _joystick[port];
}
