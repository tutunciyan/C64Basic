using System.Runtime.InteropServices;
using C64Basic.Core.Disk;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;
using Silk.NET.Core.Contexts;
using Silk.NET.SDL;

namespace C64Basic.Gui;

/// <summary>The window: SDL events in, the VIC-II frame and SID audio out. The interpreter runs on its own thread.</summary>
static unsafe partial class SdlHost
{
    const int SampleRate = 44100;

    static readonly Sdl Sdl = CreateApi();
    static AudioCallback _audioCallback = null!; // kept alive for SDL
    static Bus _bus = null!;

    /// <summary>The joystick port the numpad drives; the Pause key switches it between 1 and 2.</summary>
    static int _keyboardPort = 2;

    /// <summary>The mouse is a light pen on port 1 instead of a paddle (<c>--lightpen</c>).</summary>
    public static bool LightPen { get; set; }

    /// <summary>The file Ctrl+S and Ctrl+L use for the machine state.</summary>
    static string _stateFile = "c64-state.sav";

    /// <summary>A message from the interpreter thread for the title bar (SDL calls stay on the main thread).</summary>
    static volatile string? _statusMessage;
    static long _statusUntil;

    /// <summary>
    /// Silk.NET looks for SDL2 by bare name, which does not reach the copy in <c>runtimes/&lt;rid&gt;/native</c> that a
    /// plain build leaves on Linux and macOS (a published folder has it next to the executable). So the bundled library is
    /// loaded by path when it is there, and the system's SDL2 is the fallback.
    /// </summary>
    static Sdl CreateApi()
    {
        string rid = RuntimeInformation.RuntimeIdentifier.StartsWith("osx", StringComparison.Ordinal) ? "osx"
            : RuntimeInformation.RuntimeIdentifier.StartsWith("win", StringComparison.Ordinal) ? "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            : "linux-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string[] names = { "SDL2.dll", "libSDL2-2.0.so", "libSDL2-2.0.dylib", "libSDL2-2.0.0.dylib" };
        foreach (string dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native") })
        {
            foreach (string name in names)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) return new Sdl(new DefaultNativeContext(path));
            }
        }
        return Sdl.GetApi(); // fall back to the system's SDL2
    }

    public static int Run(Interpreter interpreter, ScreenConsole console, System.Threading.Thread worker, int scale, bool fullscreen, string? snapshot, int keyboardPort = 2, string? stateFile = null, bool resume = false)
    {
        _bus = interpreter.Bus;
        _keyboardPort = keyboardPort;
        if (stateFile != null) _stateFile = stateFile;
        _interpreterForState = interpreter;
        _consoleForState = console;

        if (Sdl.Init(Sdl.InitVideo | Sdl.InitAudio | Sdl.InitEvents | Sdl.InitGamecontroller) != 0)
        {
            Console.Error.WriteLine("SDL could not start: " + Sdl.GetErrorS());
            return 1;
        }

        uint flags = (uint)WindowFlags.Resizable | (fullscreen ? (uint)WindowFlags.FullscreenDesktop : 0);
        var window = Sdl.CreateWindow("C64 BASIC", Sdl.WindowposCentered, Sdl.WindowposCentered,
            Vic2.FrameWidth * scale, Vic2.FrameHeight * scale, flags);
        if (window == null) { Console.Error.WriteLine("No window: " + Sdl.GetErrorS()); return 1; }

        var renderer = Sdl.CreateRenderer(window, -1, (uint)RendererFlags.Accelerated | (uint)RendererFlags.Presentvsync);
        if (renderer == null) renderer = Sdl.CreateRenderer(window, -1, (uint)RendererFlags.Software);
        Sdl.RenderSetLogicalSize(renderer, Vic2.FrameWidth, Vic2.FrameHeight);
        Sdl.SetHint(Sdl.HintRenderScaleQuality, "nearest");
        var texture = Sdl.CreateTexture(renderer, (uint)PixelFormatEnum.Argb8888, (int)TextureAccess.Streaming,
            Vic2.FrameWidth, Vic2.FrameHeight);

        uint audio = OpenAudio();
        Sdl.StartTextInput();
        var gamepads = new Gamepads(Sdl, console);
        gamepads.OpenAll();
        worker.Start();
        if (resume) LoadState();

        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        var joystick = (byte)0;
        bool running = true, blinkOn = true;
        long blinkAt = Environment.TickCount64, startedAt = Environment.TickCount64;

        while (running && worker.IsAlive)
        {
            Event e;
            while (Sdl.PollEvent(&e) != 0)
            {
                switch ((EventType)e.Type)
                {
                    case EventType.Quit: running = false; break;
                    case EventType.Keydown: running &= KeyDown(e.Key, interpreter, console, window, frame, ref joystick); break;
                    case EventType.Keyup: KeyUp(e.Key, console, ref joystick); break;
                    case EventType.Textinput: TextInput(e.Text, console); break;
                    case EventType.Controllerdeviceadded:
                    case EventType.Controllerdeviceremoved:
                    case EventType.Controllerbuttondown:
                    case EventType.Controllerbuttonup:
                    case EventType.Controlleraxismotion:
                        gamepads.Handle(e);
                        break;
                    case EventType.Mousemotion: MouseMoved(window, e.Motion.X, e.Motion.Y, console); break;
                    case EventType.Mousebuttondown or EventType.Mousebuttonup: MouseButton(e.Button, console); break;
                    case EventType.Dropfile: DropFile(e.Drop.File, interpreter, console); break;
                    case EventType.Windowevent when e.Window.Event == (byte)WindowEventID.FocusLost:
                        console.ReleaseAllKeys();
                        _mouseButtons = 0;
                        joystick = 0;
                        break;
                }
            }

            if (_statusMessage is { } message)
            {
                _statusMessage = null;
                _statusUntil = Environment.TickCount64 + 3000;
                Sdl.SetWindowTitle(window, "C64 BASIC - " + message);
            }
            else if (_statusUntil != 0 && Environment.TickCount64 > _statusUntil)
            {
                _statusUntil = 0;
                Sdl.SetWindowTitle(window, interpreter.Warp ? "C64 BASIC (warp)" : "C64 BASIC");
            }

            if (LightPen && _mouseButtons != 0) StrikeLightPen(window);
            _bus.Vic.Render(frame);
            if (snapshot != null && Environment.TickCount64 - startedAt >= 2000)
            {
                Screenshot(frame, snapshot);
                running = false;
            }
            if (console.CursorVisible)
            {
                if (Environment.TickCount64 - blinkAt >= 400) { blinkOn = !blinkOn; blinkAt = Environment.TickCount64; }
                if (blinkOn) DrawCursor(frame, console.CursorRow, console.CursorColumn);
            }
            else { blinkOn = true; blinkAt = Environment.TickCount64; }

            fixed (uint* pixels = frame) Sdl.UpdateTexture(texture, null, pixels, Vic2.FrameWidth * sizeof(uint));
            Sdl.RenderClear(renderer);
            Sdl.RenderCopy(renderer, texture, null, null);
            Sdl.RenderPresent(renderer);
            if ((Sdl.GetWindowFlags(window) & (uint)WindowFlags.Minimized) != 0) Sdl.Delay(50);
            else Sdl.Delay(1);
        }

        console.Close();
        gamepads.CloseAll();
        if (audio != 0) Sdl.CloseAudioDevice(audio);
        Sdl.DestroyTexture(texture);
        Sdl.DestroyRenderer(renderer);
        Sdl.DestroyWindow(window);
        Sdl.Quit();
        return 0;
    }

    // ---------- audio ----------
    static uint OpenAudio()
    {
        _audioCallback = FillAudio;
        var wanted = new AudioSpec { Freq = SampleRate, Format = Sdl.AudioS16Sys, Channels = 1, Samples = 1024, Callback = new PfnAudioCallback(_audioCallback) };
        AudioSpec got;
        uint device = Sdl.OpenAudioDevice((byte*)null, 0, &wanted, &got, 0);
        if (device == 0)
        {
            Console.Error.WriteLine("No audio: " + Sdl.GetErrorS());
            return 0;
        }
        Sdl.PauseAudioDevice(device, 0);
        return device;
    }

    static void FillAudio(void* userdata, byte* stream, int length)
    {
        var samples = new Span<short>(stream, length / sizeof(short));
        _bus.Sound.Render(samples, SampleRate);
    }

    // ---------- keyboard ----------
    static bool KeyDown(KeyboardEvent key, Interpreter interpreter, ScreenConsole console, Window* window,
        uint[] frame, ref byte joystick)
    {
        var code = key.Keysym.Scancode;
        var mod = (Keymod)key.Keysym.Mod;
        bool shift = (mod & Keymod.Shift) != 0, control = (mod & Keymod.Ctrl) != 0, alt = (mod & Keymod.Alt) != 0;
        bool repeat = key.Repeat != 0;

        // emulator keys
        switch (code)
        {
            case Scancode.ScancodeF9 when !repeat:
                interpreter.Warp = !interpreter.Warp;
                Sdl.SetWindowTitle(window, interpreter.Warp ? "C64 BASIC (warp)" : "C64 BASIC");
                return true;
            case Scancode.ScancodeF10 when !repeat:
                console.Inject("SYS64738\r");
                return true;
            case Scancode.ScancodeF11 when !repeat:
            case Scancode.ScancodeReturn when alt && !repeat:
                ToggleFullscreen(window);
                return true;
            case Scancode.ScancodeF12 when !repeat:
                Screenshot(frame, $"c64-{DateTime.Now:yyyyMMdd-HHmmss}.bmp");
                return true;
            case Scancode.ScancodePagedown:
                console.SetRestore(true);
                return true;
            case Scancode.ScancodePause when !repeat:
                // move the numpad joystick to the other port
                console.SetJoystick(_keyboardPort, 0);
                _keyboardPort = _keyboardPort == 2 ? 1 : 2;
                console.SetJoystick(_keyboardPort, joystick);
                Sdl.SetWindowTitle(window, $"C64 BASIC (numpad = joystick {_keyboardPort})");
                return true;
            case Scancode.ScancodeEscape:
                console.BreakRequested = true;
                console.SetKey(63, true);
                return true;
        }

        // machine state: Ctrl+S saves, Ctrl+L loads
        if (!repeat && control && code == Scancode.ScancodeS) { SaveState(); return true; }
        if (!repeat && control && code == Scancode.ScancodeL) { LoadState(); return true; }

        // clipboard: Ctrl+V or Shift+Insert types the text, Ctrl+C copies the screen
        if (!repeat && ((control && code == Scancode.ScancodeV) || (shift && code == Scancode.ScancodeInsert)))
        {
            string? text = Sdl.GetClipboardTextS();
            if (!string.IsNullOrEmpty(text)) console.Paste(text);
            return true;
        }
        if (!repeat && control && code == Scancode.ScancodeC)
        {
            Sdl.SetClipboardText(console.ScreenText());
            return true;
        }

        byte bit = KeyMap.JoystickBit(code);
        if (bit != 0)
        {
            joystick |= bit;
            console.SetJoystick(_keyboardPort, joystick);
            return true;
        }

        // Shift+Commodore (Alt) switches the character set, as on a real C64
        bool isShift = code is Scancode.ScancodeLshift or Scancode.ScancodeRshift;
        bool isCommodore = code is Scancode.ScancodeLalt or Scancode.ScancodeRalt;
        if (!repeat && ((isShift && alt) || (isCommodore && shift))) console.ToggleCharacterSet();

        if (!repeat && KeyMap.Matrix.TryGetValue(code, out var matrix))
            foreach (int k in matrix) console.SetKey(k, true);

        if (KeyMap.Color(code, control, alt) is char color) console.Type(color);
        else if (!control && !alt && KeyMap.Typed(code, shift) is char typed) console.Type(typed);
        return true;
    }

    static void KeyUp(KeyboardEvent key, ScreenConsole console, ref byte joystick)
    {
        var code = key.Keysym.Scancode;
        if (code == Scancode.ScancodeEscape) console.SetKey(63, false);
        if (code == Scancode.ScancodePagedown) console.SetRestore(false);

        byte bit = KeyMap.JoystickBit(code);
        if (bit != 0)
        {
            joystick &= (byte)~bit;
            console.SetJoystick(_keyboardPort, joystick);
            return;
        }
        if (KeyMap.Matrix.TryGetValue(code, out var matrix))
            foreach (int k in matrix) console.SetKey(k, false);
    }

    // ---------- the mouse is a paddle ----------
    // Its position is the paddle on the port the numpad drives (POTX/POTY at $D419/$D41A once $DC00 bits 7-6 select that port);
    // the left button is fire A and the right button fire B (the joystick's left and right bits). It has its own input source,
    // so it never disturbs the keyboard or a game controller.
    const int MouseSource = 6;
    static byte _mouseButtons;

    static int _mouseX, _mouseY;

    /// <summary>Where a window position is in the picture, as the beam sees it: sprite X (frame x - 8) and the raster line (frame row + 15).</summary>
    static bool BeamAt(Window* window, out int x, out int line)
    {
        int width, height;
        Sdl.GetWindowSize(window, &width, &height);
        double scale = Math.Min((double)width / Vic2.FrameWidth, (double)height / Vic2.FrameHeight);
        double left = (width - Vic2.FrameWidth * scale) / 2, top = (height - Vic2.FrameHeight * scale) / 2;
        int fx = (int)((_mouseX - left) / scale), fy = (int)((_mouseY - top) / scale);
        x = fx - 8;
        line = fy + 15;
        return scale > 0 && fx >= 0 && fx < Vic2.FrameWidth && fy >= 0 && fy < Vic2.FrameHeight;
    }

    /// <summary>A light pen strike for the frame being drawn while the button is down: the first one per frame latches.</summary>
    static void StrikeLightPen(Window* window)
    {
        if (BeamAt(window, out int x, out int line)) _bus.Vic.LightPen(x, line);
    }

    static void MouseMoved(Window* window, int x, int y, IGameInput console)
    {
        _mouseX = x; _mouseY = y;
        if (LightPen) return;
        int width, height;
        Sdl.GetWindowSize(window, &width, &height);
        if (width <= 0 || height <= 0) return;
        console.SetPaddle(_keyboardPort, 0, x * 255 / width);
        console.SetPaddle(_keyboardPort, 1, y * 255 / height);
    }

    static void MouseButton(MouseButtonEvent button, IGameInput console)
    {
        if (LightPen)
        {
            // the pen's switch is the joystick fire button on port 1 (it also pulls CIA 1 PB4 low)
            if (button.Button == 1)
            {
                _mouseButtons = (byte)(button.State != 0 ? 1 : 0);
                console.SetJoystick(1, (byte)(_mouseButtons != 0 ? JoystickMapping.Fire : 0), MouseSource);
            }
            return;
        }
        byte bit = button.Button == 1 ? (byte)JoystickMapping.Left : button.Button == 3 ? (byte)JoystickMapping.Right : (byte)0;
        if (bit == 0) return;
        if (button.State != 0) _mouseButtons |= bit; else _mouseButtons &= (byte)~bit;
        console.SetJoystick(_keyboardPort, _mouseButtons, MouseSource);
    }

    static void TextInput(TextInputEvent text, ScreenConsole console)
    {
        string typed = Marshal.PtrToStringUTF8((nint)text.Text) ?? "";
        // Shift+letter is a graphics symbol on a C64 (a capital in the lower-case set), as on the real keyboard
        bool shift = (Sdl.GetModState() & Keymod.Shift) != 0;
        foreach (char c in typed)
        {
            if (c < ' ' || (c >= '\u007f' && c != '£')) continue;
            console.Type(shift ? Petscii.ShiftedLetter(c) : c);
        }
    }

    // ---------- machine state ----------
    static Interpreter _interpreterForState = null!;
    static ScreenConsole _consoleForState = null!;

    static void SaveState()
    {
        string file = _stateFile;
        _interpreterForState.SaveStateLater(bytes =>
        {
            try { File.WriteAllBytes(file, bytes); _statusMessage = $"state saved ({Path.GetFileName(file)})"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _statusMessage = "could not save: " + e.Message; }
        });
    }

    static void LoadState()
    {
        byte[] data;
        try { data = File.ReadAllBytes(_stateFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _statusMessage = "no saved state (" + Path.GetFileName(_stateFile) + ")";
            return;
        }
        _interpreterForState.LoadStateLater(data, (result, error) =>
        {
            if (error != null) { _statusMessage = "could not load: " + error.Message; return; }
            _statusMessage = "state loaded";
            // a program that was running when saved carries on: the prompt is waiting, so type CONT for it
            if (result is { NeedsContinue: true }) _consoleForState.Inject("CONT" + (char)13);
        });
    }

    // ---------- window ----------
    static void ToggleFullscreen(Window* window)
    {
        bool full = (Sdl.GetWindowFlags(window) & (uint)WindowFlags.FullscreenDesktop) != 0;
        Sdl.SetWindowFullscreen(window, full ? 0 : (uint)WindowFlags.FullscreenDesktop);
    }

    /// <summary>The cursor is the character cell drawn in reverse.</summary>
    static void DrawCursor(uint[] frame, int row, int column)
    {
        int x0 = (Vic2.FrameWidth - Vic2.DisplayWidth) / 2 + column * 8;
        int y0 = (Vic2.FrameHeight - Vic2.DisplayHeight) / 2 + row * 8;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                frame[(y0 + y) * Vic2.FrameWidth + x0 + x] ^= 0x00FFFFFF;
    }

    static void DropFile(byte* file, Interpreter interpreter, ScreenConsole console)
    {
        string? path = Marshal.PtrToStringUTF8((nint)file);
        Sdl.Free(file);
        if (path == null) return;

        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".d64":
                case ".g64":
                    interpreter.MountDrive(8, ImageFiles.OpenDisk(path));
                    console.Inject("\u0093" + "LOAD\"$\",8\rLIST\r");
                    break;
                case ".t64":
                case ".tap":
                    interpreter.MountDrive(1, ImageFiles.OpenTape(path));
                    break;
                case ".prg":
                    {
                        var bytes = File.ReadAllBytes(path);
                        bool basic = PrgFormat.LoadAddress(bytes) == PrgFormat.BasicStart;
                        console.Inject(basic ? $"LOAD\"{path}\"\rRUN\r" : $"LOAD\"{path}\",8,1\r");
                        break;
                    }
                case ".sav":
                    File.Copy(path, _stateFile, overwrite: true);
                    LoadState();
                    break;
                case ".bas":
                    console.Inject($"LOAD\"{path}\"\rRUN\r");
                    break;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
        }
    }

    /// <summary>Saves the last frame as a 24-bit BMP next to the program.</summary>
    static void Screenshot(uint[] frame, string path)
    {
        int w = Vic2.FrameWidth, h = Vic2.FrameHeight, stride = w * 3;
        var data = new byte[54 + stride * h];
        BitConverter.GetBytes((ushort)0x4D42).CopyTo(data, 0);
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(w).CopyTo(data, 18);
        BitConverter.GetBytes(h).CopyTo(data, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(data, 28);
        BitConverter.GetBytes(stride * h).CopyTo(data, 34);
        for (int y = 0; y < h; y++)
        {
            int row = 54 + (h - 1 - y) * stride;           // BMP rows run bottom to top
            for (int x = 0; x < w; x++)
            {
                uint p = frame[y * w + x];
                data[row + x * 3] = (byte)p; data[row + x * 3 + 1] = (byte)(p >> 8); data[row + x * 3 + 2] = (byte)(p >> 16);
            }
        }
        File.WriteAllBytes(path, data);
    }
}
