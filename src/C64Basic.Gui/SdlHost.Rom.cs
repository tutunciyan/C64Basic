using System.Runtime.InteropServices;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Rom;
using Silk.NET.SDL;

namespace C64Basic.Gui;

/// <summary>The window for ROM mode: the machine runs the real ROMs on its own thread and every key goes through the keyboard matrix.</summary>
static unsafe partial class SdlHost
{
    static volatile bool _romWarp;

    public static int RunRom(RomMachine machine, int scale, bool fullscreen, string? snapshot, int keyboardPort = 2,
        bool warp = false)
    {
        _bus = machine.Bus;
        _keyboardPort = keyboardPort;
        _romWarp = warp;

        if (Sdl.Init(Sdl.InitVideo | Sdl.InitAudio | Sdl.InitEvents | Sdl.InitGamecontroller) != 0)
        {
            Console.Error.WriteLine("SDL could not start: " + Sdl.GetErrorS());
            return 1;
        }

        uint flags = (uint)WindowFlags.Resizable | (fullscreen ? (uint)WindowFlags.FullscreenDesktop : 0);
        var window = Sdl.CreateWindow("C64 (ROM mode)", Sdl.WindowposCentered, Sdl.WindowposCentered,
            Vic2.FrameWidth * scale, Vic2.FrameHeight * scale, flags);
        if (window == null) { Console.Error.WriteLine("No window: " + Sdl.GetErrorS()); return 1; }

        var renderer = Sdl.CreateRenderer(window, -1, (uint)RendererFlags.Accelerated | (uint)RendererFlags.Presentvsync);
        if (renderer == null) renderer = Sdl.CreateRenderer(window, -1, (uint)RendererFlags.Software);
        Sdl.RenderSetLogicalSize(renderer, Vic2.FrameWidth, Vic2.FrameHeight);
        Sdl.SetHint(Sdl.HintRenderScaleQuality, "nearest");
        var texture = Sdl.CreateTexture(renderer, (uint)PixelFormatEnum.Argb8888, (int)TextureAccess.Streaming,
            Vic2.FrameWidth, Vic2.FrameHeight);

        uint audio = OpenAudio();
        var gamepads = new Gamepads(Sdl, machine.Input);
        gamepads.OpenAll();

        bool running = true;
        var worker = new System.Threading.Thread(() => machine.RunPaced(() => !running, () => _romWarp))
        { IsBackground = true, Name = "C64 ROM mode" };
        worker.Start();

        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        var joystick = (byte)0;
        long startedAt = Environment.TickCount64;

        while (running)
        {
            Event e;
            while (Sdl.PollEvent(&e) != 0)
            {
                switch ((EventType)e.Type)
                {
                    case EventType.Quit: running = false; break;
                    case EventType.Keydown: RomKeyDown(e.Key, machine, window, frame, ref joystick); break;
                    case EventType.Keyup: RomKeyUp(e.Key, machine, ref joystick); break;
                    case EventType.Controllerdeviceadded:
                    case EventType.Controllerdeviceremoved:
                    case EventType.Controllerbuttondown:
                    case EventType.Controllerbuttonup:
                    case EventType.Controlleraxismotion:
                        gamepads.Handle(e);
                        break;
                    case EventType.Mousemotion: MouseMoved(window, e.Motion.X, e.Motion.Y, machine.Input); break;
                    case EventType.Mousebuttondown or EventType.Mousebuttonup: MouseButton(e.Button, machine.Input); break;
                    case EventType.Dropfile: RomDropFile(e.Drop.File, machine); break;
                    case EventType.Windowevent when e.Window.Event == (byte)WindowEventID.FocusLost:
                        machine.Input.ReleaseAllKeys();
                        _mouseButtons = 0;
                        joystick = 0;
                        break;
                }
            }

            if (_statusMessage is { } message)
            {
                _statusMessage = null;
                _statusUntil = Environment.TickCount64 + 3000;
                Sdl.SetWindowTitle(window, "C64 (ROM mode) - " + message);
            }
            else if (_statusUntil != 0 && Environment.TickCount64 > _statusUntil)
            {
                _statusUntil = 0;
                Sdl.SetWindowTitle(window, _romWarp ? "C64 (ROM mode, warp)" : "C64 (ROM mode)");
            }
            if (machine.Halted && _statusUntil == 0) Sdl.SetWindowTitle(window, "C64 (ROM mode) - stopped: " + machine.HaltReason);

            if (LightPen && _mouseButtons != 0) StrikeLightPen(window);
            _bus.Vic.Render(frame);
            if (snapshot != null && Environment.TickCount64 - startedAt >= 2000)
            {
                Screenshot(frame, snapshot);
                running = false;
            }

            fixed (uint* pixels = frame) Sdl.UpdateTexture(texture, null, pixels, Vic2.FrameWidth * sizeof(uint));
            Sdl.RenderClear(renderer);
            Sdl.RenderCopy(renderer, texture, null, null);
            Sdl.RenderPresent(renderer);
            if ((Sdl.GetWindowFlags(window) & (uint)WindowFlags.Minimized) != 0) Sdl.Delay(50);
            else Sdl.Delay(1);
        }

        worker.Join(1000);
        gamepads.CloseAll();
        if (audio != 0) Sdl.CloseAudioDevice(audio);
        Sdl.DestroyTexture(texture);
        Sdl.DestroyRenderer(renderer);
        Sdl.DestroyWindow(window);
        Sdl.Quit();
        return 0;
    }

    static void RomKeyDown(KeyboardEvent key, RomMachine machine, Window* window, uint[] frame, ref byte joystick)
    {
        var code = key.Keysym.Scancode;
        var mod = (Keymod)key.Keysym.Mod;
        bool shift = (mod & Keymod.Shift) != 0, control = (mod & Keymod.Ctrl) != 0, alt = (mod & Keymod.Alt) != 0;
        bool repeat = key.Repeat != 0;

        switch (code)
        {
            case Scancode.ScancodeF9 when !repeat:
                _romWarp = !_romWarp;
                Sdl.SetWindowTitle(window, _romWarp ? "C64 (ROM mode, warp)" : "C64 (ROM mode)");
                return;
            case Scancode.ScancodeF10 when !repeat:
                machine.Post(machine.Reset);
                return;
            case Scancode.ScancodeF11 when !repeat:
            case Scancode.ScancodeReturn when alt && !repeat:
                ToggleFullscreen(window);
                return;
            case Scancode.ScancodeF12 when !repeat:
                Screenshot(frame, $"c64-{DateTime.Now:yyyyMMdd-HHmmss}.bmp");
                return;
            case Scancode.ScancodePagedown:
                machine.Input.SetRestore(true);
                return;
            case Scancode.ScancodePause when !repeat:
                machine.Input.SetJoystick(_keyboardPort, 0);
                _keyboardPort = _keyboardPort == 2 ? 1 : 2;
                machine.Input.SetJoystick(_keyboardPort, joystick);
                Sdl.SetWindowTitle(window, $"C64 (ROM mode, numpad = joystick {_keyboardPort})");
                return;
        }

        if (!repeat && ((control && code == Scancode.ScancodeV) || (shift && code == Scancode.ScancodeInsert)))
        {
            string? text = Sdl.GetClipboardTextS();
            if (!string.IsNullOrEmpty(text)) machine.Type(PasteText(text));
            return;
        }
        if (!repeat && control && code == Scancode.ScancodeC)
        {
            Sdl.SetClipboardText(machine.ScreenText());
            return;
        }

        byte bit = KeyMap.JoystickBit(code);
        if (bit != 0)
        {
            joystick |= bit;
            machine.Input.SetJoystick(_keyboardPort, joystick);
            return;
        }

        if (!repeat && KeyMap.Matrix.TryGetValue(code, out var matrix))
            foreach (int k in matrix) machine.Input.SetKey(k, true);
    }

    static void RomKeyUp(KeyboardEvent key, RomMachine machine, ref byte joystick)
    {
        var code = key.Keysym.Scancode;
        if (code == Scancode.ScancodePagedown) machine.Input.SetRestore(false);

        byte bit = KeyMap.JoystickBit(code);
        if (bit != 0)
        {
            joystick &= (byte)~bit;
            machine.Input.SetJoystick(_keyboardPort, joystick);
            return;
        }
        if (KeyMap.Matrix.TryGetValue(code, out var matrix))
            foreach (int k in matrix) machine.Input.SetKey(k, false);
    }

    /// <summary>Clipboard text as the keyboard buffer takes it: line breaks become RETURN, what a C64 cannot type is dropped.</summary>
    static string PasteText(string text)
    {
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, ScreenConsole.MaxPaste));
        for (int i = 0; i < text.Length && sb.Length < ScreenConsole.MaxPaste; i++)
        {
            char c = text[i];
            if (c == '\r') { sb.Append('\r'); if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c == '\n') sb.Append('\r');
            else if (c == '\t') sb.Append(' ');
            else if ((c >= ' ' && c < '\u007f') || c == '£') sb.Append(c);
        }
        return sb.ToString();
    }

    static void RomDropFile(byte* file, RomMachine machine)
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
                    {
                        var image = File.ReadAllBytes(path);
                        machine.Post(() => machine.MountDisk(image));
                        _statusMessage = "disk mounted: " + Path.GetFileName(path);
                        break;
                    }
                default:
                    _statusMessage = "ROM mode takes disk images (.d64, .g64)";
                    break;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
        }
    }
}
