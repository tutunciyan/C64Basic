using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace C64Basic.Gui;

/// <summary>
/// The window's own interface on top of the C64 picture: a toolbar of buttons along the top, and the mouse for text: Shift + drag picks
/// text off the screen and copies it, a middle click pastes. Both windows (the interpreter's and ROM mode's) use it.
/// </summary>
static unsafe partial class SdlHost
{
    /// <summary>One button of the toolbar. <see cref="On"/> says whether a toggle is switched on (the button is drawn lit).</summary>
    sealed class ToolButton
    {
        public required string Label { get; set; }
        public required string Hint { get; init; }
        public required Action Click { get; init; }
        public Func<bool>? On { get; init; }

        /// <summary>The picture on the button (see <see cref="ToolIcons"/>); without one the label is shown as text.</summary>
        public string[]? Icon { get; init; }

        /// <summary>A character shown beside the picture (the port the joystick is on).</summary>
        public Func<string>? Suffix { get; init; }
        public int X, Width;                                   // in window pixels, set by the layout
    }

    /// <summary>The toolbar's height in glyph-scale pixels: a 16 pixel picture with room above and below.</summary>
    public const int BarUnits = 24;

    /// <summary>The toolbar can be turned off (<c>--no-toolbar</c>, Ctrl+F12); it is always out of the way in full screen.</summary>
    public static bool ToolbarEnabled { get; set; } = true;

    static List<ToolButton> _buttons = new();
    static Func<string[]> _screenRows = () => new string[ScreenSelection.Rows];
    static Action<string> _pasteText = _ => { };

    static int _outWidth, _outHeight, _barHeight, _glyphScale = 1;
    static int _frameX, _frameY, _frameWidth = Vic2.FrameWidth, _frameHeight = Vic2.FrameHeight;
    static Texture* _barTexture;
    static int _barTextureWidth, _barTextureHeight;
    static uint[] _barPixels = Array.Empty<uint>();
    static int _hover = -1, _pressed = -1;
    static ScreenSelection? _selection;
    static bool _selecting;

    static void SetUp(IEnumerable<ToolButton> buttons, Func<string[]> screenRows, Action<string> pasteText)
    {
        _buttons = buttons.ToList();
        _screenRows = screenRows;
        _pasteText = pasteText;
        _selection = null;
        _selecting = false;
        _hover = _pressed = -1;
    }

    // ---------- the cursor keys as a joystick ----------
    // A game that wants a joystick can be played from the keyboard: with this on, the cursor keys are the stick and Space (or Right Ctrl)
    // fire, on port 1 or port 2 (games differ: Ms. Pac-Man reads port 1, many read port 2). The numpad follows to the same port.
    static ToolButton? _joyButton;
    static int _cursorJoystick;                 // 0 = off (the cursor keys are C64 cursor keys), 1 or 2 = the port
    static volatile bool _joystickReset;

    static byte JoystickBitFor(Scancode code)
    {
        byte bit = KeyMap.JoystickBit(code);
        if (bit != 0 || _cursorJoystick == 0) return bit;
        return code switch
        {
            Scancode.ScancodeUp => 1,
            Scancode.ScancodeDown => 2,
            Scancode.ScancodeLeft => 4,
            Scancode.ScancodeRight => 8,
            Scancode.ScancodeSpace or Scancode.ScancodeRctrl => 16,
            _ => 0,
        };
    }

    /// <summary>JOY button / Ctrl+J: off, then cursor keys on port 1, then on port 2.</summary>
    static void CycleCursorJoystick(IGameInput input, ToolButton? button)
    {
        input.SetJoystick(_keyboardPort, 0);
        _cursorJoystick = (_cursorJoystick + 1) % 3;
        if (_cursorJoystick != 0) _keyboardPort = _cursorJoystick;
        _joystickReset = true;
        if (button != null) button.Label = _cursorJoystick == 0 ? "JOY-" : "JOY" + _cursorJoystick;
        _statusMessage = _cursorJoystick == 0 ? "cursor keys are cursor keys again"
            : $"cursor keys = joystick {_cursorJoystick}, Space = fire (the numpad too)";
    }

    // ---------- the Open dialog ----------
    static volatile string? _pickedFile;
    static int _dialogOpen;

    /// <summary>Shows the system's file dialog on a thread of its own (it blocks); the pick is handed back to the window loop through <see cref="TakePickedFile"/>.</summary>
    static void AskForFile()
    {
        if (System.Threading.Interlocked.Exchange(ref _dialogOpen, 1) == 1) return;      // one at a time
        _statusMessage = "choose a file: disk, tape, cartridge, program or state";
        new System.Threading.Thread(() =>
        {
            try
            {
                string? path = FileDialog.Open("Open a disk, tape, cartridge or program", out string? problem);
                if (path != null) _pickedFile = path;
                else if (problem != null) _statusMessage = problem;
            }
            finally { _dialogOpen = 0; }
        }) { IsBackground = true, Name = "file dialog" }.Start();
    }

    /// <summary>The file chosen in the dialog since the last call, or null.</summary>
    static string? TakePickedFile() => System.Threading.Interlocked.Exchange(ref _pickedFile, null);

    // ---------- the selection and the clipboard ----------
    /// <summary>The selected text, or the whole screen when nothing is selected.</summary>
    static string TextToCopy()
    {
        var rows = _screenRows();
        if (_selection is { IsRange: true } selection) return selection.Extract(rows);
        var trimmed = rows.Select(r => r.TrimEnd(' ')).ToList();
        while (trimmed.Count > 0 && trimmed[^1].Length == 0) trimmed.RemoveAt(trimmed.Count - 1);
        return string.Join("\n", trimmed);
    }

    static void CopyToClipboard()
    {
        string text = TextToCopy();
        Sdl.SetClipboardText(text);
        _statusMessage = _selection is { IsRange: true } ? $"copied {text.Length} characters" : "copied the screen";
    }

    static void PasteFromClipboard()
    {
        string? text = Sdl.GetClipboardTextS();
        if (string.IsNullOrEmpty(text)) { _statusMessage = "the clipboard has no text"; return; }
        _pasteText(text);
        _statusMessage = $"pasted {Math.Min(text.Length, ScreenConsole.MaxPaste)} characters";
    }

    /// <summary>Inverts the selected cells of the frame, as the cursor is.</summary>
    static void DrawSelection(uint[] frame)
    {
        if (_selection is not { IsRange: true } selection) return;
        for (int row = 0; row < ScreenSelection.Rows; row++)
            for (int column = 0; column < ScreenSelection.Columns; column++)
            {
                if (!selection.Contains(column, row)) continue;
                int x0 = 32 + column * 8, y0 = 36 + row * 8;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        frame[(y0 + y) * Vic2.FrameWidth + x0 + x] ^= 0x00FFFFFF;
            }
    }

    static void ClearSelection() { _selection = null; _selecting = false; }

    // ---------- layout ----------
    static bool ToolbarShown(Window* window) => ToolbarEnabled && _buttons.Count > 0 && !IsFullscreen(window);

    /// <summary>Works out where the toolbar and the picture go in a window of the given size in pixels.</summary>
    static void Layout(int width, int height, bool toolbar)
    {
        _outWidth = width; _outHeight = height;
        _glyphScale = width >= 1100 ? 2 : 1;
        _barHeight = toolbar ? BarUnits * _glyphScale : 0;
        int x = 0;
        foreach (var button in _buttons)
        {
            button.Width = (button.Icon != null ? ToolIcons.Size + 8 + (button.Suffix != null ? 8 : 0) : button.Label.Length * 8 + 8) * _glyphScale;
            button.X = x;
            x += button.Width + 2 * _glyphScale;
        }
        int availableHeight = Math.Max(1, height - _barHeight);
        double scale = Math.Min((double)width / Vic2.FrameWidth, (double)availableHeight / Vic2.FrameHeight);
        _frameWidth = Math.Max(1, (int)(Vic2.FrameWidth * scale));
        _frameHeight = Math.Max(1, (int)(Vic2.FrameHeight * scale));
        _frameX = (width - _frameWidth) / 2;
        _frameY = _barHeight + (availableHeight - _frameHeight) / 2;
    }

    static int ButtonAt(int x, int y)
    {
        if (y < 0 || y >= _barHeight) return -1;
        for (int i = 0; i < _buttons.Count; i++)
            if (x >= _buttons[i].X && x < _buttons[i].X + _buttons[i].Width && _buttons[i].X + _buttons[i].Width <= _outWidth) return i;
        return -1;
    }

    /// <summary>A window position (pixels) as a point of the 384x272 frame, or false outside the picture.</summary>
    static bool FramePoint(int x, int y, out int frameX, out int frameY, bool clamp = false)
    {
        frameX = (int)((long)(x - _frameX) * Vic2.FrameWidth / _frameWidth);
        frameY = (int)((long)(y - _frameY) * Vic2.FrameHeight / _frameHeight);
        bool inside = frameX >= 0 && frameX < Vic2.FrameWidth && frameY >= 0 && frameY < Vic2.FrameHeight;
        if (clamp)
        {
            frameX = Math.Clamp(frameX, 0, Vic2.FrameWidth - 1);
            frameY = Math.Clamp(frameY, 0, Vic2.FrameHeight - 1);
        }
        return inside;
    }

    // ---------- the mouse ----------
    /// <summary>Window coordinates (in points) to renderer pixels, which differ on a high-density display.</summary>
    static (int X, int Y) ToPixels(Window* window, int x, int y)
    {
        int width, height;
        Sdl.GetWindowSize(window, &width, &height);
        if (width <= 0 || height <= 0 || _outWidth <= 0) return (x, y);
        return ((int)((long)x * _outWidth / width), (int)((long)y * _outHeight / height));
    }

    /// <summary>Records the pointer and tells whether the toolbar or a selection used the movement (then the paddle ignores it).</summary>
    static bool UiMouseMoved(Window* window, int x, int y)
    {
        (_mouseX, _mouseY) = ToPixels(window, x, y);
        int over = ButtonAt(_mouseX, _mouseY);
        if (over != _hover)
        {
            _hover = over;
            if (over >= 0) _statusMessage = _buttons[over].Hint;
        }
        if (_selecting && _selection != null)
        {
            FramePoint(_mouseX, _mouseY, out int fx, out int fy, clamp: true);
            var cell = ScreenSelection.CellAt(fx, fy, clamp: true)!.Value;
            _selection.Extend(cell.Column, cell.Row);
            return true;
        }
        return _barHeight > 0 && _mouseY < _barHeight;
    }

    /// <summary>The toolbar's buttons, Shift + left drag to select text, a middle click to paste. True when the event was used.</summary>
    static bool UiMouseButton(Window* window, MouseButtonEvent button)
    {
        (_mouseX, _mouseY) = ToPixels(window, button.X, button.Y);
        bool down = button.State != 0;
        bool shift = (Sdl.GetModState() & Keymod.Shift) != 0;

        if (button.Button == 1 && _barHeight > 0 && (_mouseY < _barHeight || _pressed >= 0))
        {
            int over = ButtonAt(_mouseX, _mouseY);
            if (down) _pressed = over;
            else
            {
                if (_pressed >= 0 && _pressed == over) _buttons[over].Click();
                _pressed = -1;
            }
            return true;
        }

        if (button.Button == 1 && down && shift && FramePoint(_mouseX, _mouseY, out int fx, out int fy) && ScreenSelection.CellAt(fx, fy) is { } cell)
        {
            _selection = new ScreenSelection(cell.Column, cell.Row);
            _selecting = true;
            return true;
        }
        if (button.Button == 1 && !down && _selecting)
        {
            _selecting = false;
            if (_selection is { IsRange: true }) CopyToClipboard();
            else _selection = null;
            return true;
        }
        if (button.Button == 2 && down && FramePoint(_mouseX, _mouseY, out _, out _))
        {
            PasteFromClipboard();
            return true;
        }
        if (button.Button == 1 && down) _selection = null;     // an ordinary click drops the highlight
        return false;
    }

    // ---------- drawing ----------
    /// <summary>Uploads the frame, draws the toolbar above it and the picture below, and shows the result.</summary>
    static void Present(Renderer* renderer, Window* window, Texture* texture, uint[] frame)
    {
        int width, height;
        Sdl.GetRendererOutputSize(renderer, &width, &height);
        bool toolbar = ToolbarShown(window);
        if (width != _outWidth || height != _outHeight || (toolbar ? 1 : 0) != Math.Min(1, _barHeight)) Layout(width, height, toolbar);

        DrawSelection(frame);
        fixed (uint* pixels = frame) Sdl.UpdateTexture(texture, null, pixels, Vic2.FrameWidth * sizeof(uint));
        Sdl.RenderClear(renderer);
        var target = new Rectangle<int>(_frameX, _frameY, _frameWidth, _frameHeight);
        Sdl.RenderCopy(renderer, texture, null, &target);
        if (_barHeight > 0) DrawToolbar(renderer);
        Sdl.RenderPresent(renderer);
        UnDrawSelection(frame);
    }

    static void UnDrawSelection(uint[] frame) => DrawSelection(frame);        // the same inversion again puts the picture back

    const uint BarBackground = 0xFF23232D, ButtonColor = 0xFF42425E, HoverColor = 0xFF5A5A84, OnColor = 0xFF3B8A3B, PressedColor = 0xFF8484C4, TextColor = 0xFFEAEAF2;

    static void DrawToolbar(Renderer* renderer)
    {
        if (_barTexture == null || _barTextureWidth != _outWidth || _barTextureHeight != _barHeight)
        {
            if (_barTexture != null) Sdl.DestroyTexture(_barTexture);
            _barTexture = Sdl.CreateTexture(renderer, (uint)PixelFormatEnum.Argb8888, (int)TextureAccess.Streaming, _outWidth, _barHeight);
            _barTextureWidth = _outWidth; _barTextureHeight = _barHeight;
            _barPixels = new uint[_outWidth * _barHeight];
        }
        Array.Fill(_barPixels, BarBackground);
        int s = _glyphScale;
        for (int i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i];
            if (b.X + b.Width > _outWidth) break;                               // no room: the key still works
            uint colour = i == _pressed && i == _hover ? PressedColor : b.On?.Invoke() == true ? OnColor : i == _hover ? HoverColor : ButtonColor;
            FillRectangle(b.X, 2 * s, b.Width, _barHeight - 4 * s, colour);
            if (b.Icon != null)
            {
                DrawIcon(b.Icon, b.X + 4 * s, (_barHeight - ToolIcons.Size * s) / 2);
                if (b.Suffix != null) DrawText(b.Suffix(), b.X + (4 + ToolIcons.Size) * s, (_barHeight - 8 * s) / 2, TextColor);
            }
            else DrawText(b.Label, b.X + 4 * s, (_barHeight - 8 * s) / 2, TextColor);
        }
        fixed (uint* pixels = _barPixels) Sdl.UpdateTexture(_barTexture, null, pixels, _outWidth * sizeof(uint));
        var target = new Rectangle<int>(0, 0, _outWidth, _barHeight);
        Sdl.RenderCopy(renderer, _barTexture, null, &target);
    }

    const uint AccentColor = 0xFFF0C060;

    /// <summary>A 16 by 16 picture of '#' (main colour) and 'o' (accent) pixels at the glyph scale.</summary>
    static void DrawIcon(string[] icon, int x, int y)
    {
        int s = _glyphScale;
        for (int row = 0; row < ToolIcons.Size && row < icon.Length; row++)
            for (int column = 0; column < ToolIcons.Size && column < icon[row].Length; column++)
            {
                char c = icon[row][column];
                if (c != '#' && c != 'o') continue;
                FillRectangle(x + column * s, y + row * s, s, s, c == '#' ? TextColor : AccentColor);
            }
    }

    static void FillRectangle(int x, int y, int width, int height, uint colour)
    {
        for (int row = Math.Max(0, y); row < Math.Min(_barHeight, y + height); row++)
            for (int column = Math.Max(0, x); column < Math.Min(_outWidth, x + width); column++)
                _barPixels[row * _outWidth + column] = colour;
    }

    /// <summary>Text in the C64's own character set (upper case, digits and punctuation), at the glyph scale.</summary>
    static void DrawText(string text, int x, int y, uint colour)
    {
        int s = _glyphScale;
        var rom = _bus.CharacterRom;
        foreach (char c in text)
        {
            int code = c is >= 'A' and <= 'Z' ? c - 64 : c is >= 'a' and <= 'z' ? c - 96 : c < 64 ? c : 32;
            for (int row = 0; row < 8; row++)
            {
                int bits = rom[code * 8 + row];
                for (int column = 0; column < 8; column++)
                {
                    if ((bits >> (7 - column) & 1) == 0) continue;
                    for (int dy = 0; dy < s; dy++)
                        for (int dx = 0; dx < s; dx++)
                        {
                            int px = x + column * s + dx, py = y + row * s + dy;
                            if (px >= 0 && px < _outWidth && py >= 0 && py < _barHeight) _barPixels[py * _outWidth + px] = colour;
                        }
                }
            }
            x += 8 * s;
        }
    }
}
