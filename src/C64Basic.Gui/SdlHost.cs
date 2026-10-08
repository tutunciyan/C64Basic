using System.Runtime.InteropServices;
using C64Basic.Core.Disk;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;
using Silk.NET.Core.Contexts;
using Silk.NET.SDL;

namespace C64Basic.Gui;

/// <summary>The window: SDL events in, the VIC-II frame and SID audio out. The interpreter runs on its own thread.</summary>
static unsafe class SdlHost
{
    const int SampleRate = 44100;

    static readonly Sdl Sdl = CreateApi();
    static AudioCallback _audioCallback = null!; // kept alive for SDL
    static Bus _bus = null!;

    /// <summary>The joystick port the numpad drives; the Pause key switches it between 1 and 2.</summary>
    static int _keyboardPort = 2;

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

    public static int Run(Interpreter interpreter, ScreenConsole console, System.Threading.Thread worker, int scale, bool fullscreen, string? snapshot, int keyboardPort = 2)
    {
        _bus = interpreter.Bus;
        _keyboardPort = keyboardPort;

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
                    case EventType.Dropfile: DropFile(e.Drop.File, interpreter, console); break;
                    case EventType.Windowevent when e.Window.Event == (byte)WindowEventID.FocusLost:
                        console.ReleaseAllKeys();
                        joystick = 0;
                        break;
                }
            }

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
