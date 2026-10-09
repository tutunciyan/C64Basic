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

    /// <summary>The file Ctrl+S and Ctrl+L use for the state of ROM mode.</summary>
    static string _romStateFile = "c64rom-state.sav";

    public static int RunRom(RomMachine machine, int scale, bool fullscreen, string? snapshot, int keyboardPort = 2,
        bool warp = false, string? stateFile = null, bool resume = false)
    {
        _bus = machine.Bus;
        _keyboardPort = keyboardPort;
        _romWarp = warp;
        if (stateFile != null) _romStateFile = stateFile;

        if (Sdl.Init(Sdl.InitVideo | Sdl.InitAudio | Sdl.InitEvents | Sdl.InitGamecontroller) != 0)
        {
            Console.Error.WriteLine("SDL could not start: " + Sdl.GetErrorS());
            return 1;
        }

        uint flags = (uint)WindowFlags.Resizable | (fullscreen ? (uint)WindowFlags.FullscreenDesktop : 0);
        var window = Sdl.CreateWindow("C64 (ROM mode)", Sdl.WindowposCentered, Sdl.WindowposCentered,
            Vic2.FrameWidth * scale, Vic2.FrameHeight * scale + (ToolbarEnabled && !fullscreen ? 16 : 0), flags);
        if (window == null) { Console.Error.WriteLine("No window: " + Sdl.GetErrorS()); return 1; }

        var renderer = Sdl.CreateRenderer(window, -1, (uint)RendererFlags.Accelerated | (uint)RendererFlags.Presentvsync);
        if (renderer == null) renderer = Sdl.CreateRenderer(window, -1, (uint)RendererFlags.Software);
        Sdl.SetHint(Sdl.HintRenderScaleQuality, "nearest");
        var texture = Sdl.CreateTexture(renderer, (uint)PixelFormatEnum.Argb8888, (int)TextureAccess.Streaming,
            Vic2.FrameWidth, Vic2.FrameHeight);

        uint audio = OpenAudio();
        var gamepads = new Gamepads(Sdl, machine.Input);
        gamepads.OpenAll();

        bool running = true;
        machine.DiskWritten += _ => _statusMessage = machine.SaveError != null ? "could not save the disk: " + machine.SaveError
            : machine.DiskSavesChanges ? "disk saved" : "disk written (not saved: a G64 is read only, see --write-g64)";
        var worker = new System.Threading.Thread(() => machine.RunPaced(() => !running, () => _romWarp))
        { IsBackground = true, Name = "C64 ROM mode" };
        worker.Start();
        if (resume) LoadRomState(machine);

        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        var buttons = new List<ToolButton>
        {
            new() { Label = "OPEN", Hint = "pick a disk, tape, cartridge or program file (Ctrl+O)", Click = AskForFile },
            new() { Label = "RESET", Hint = "reset the machine (F10)", Click = () => machine.Post(machine.Reset) },
            new() { Label = "WARP", Hint = "warp speed (F9)", Click = () => _romWarp = !_romWarp, On = () => _romWarp },
            new() { Label = "PAUSE", Hint = "pause and show the registers (Scroll Lock)", Click = () => RomTogglePause(machine), On = () => machine.Paused },
            new() { Label = "SAVE", Hint = "save the machine state (Ctrl+S)", Click = () => SaveRomState(machine) },
            new() { Label = "LOAD", Hint = "load the machine state (Ctrl+L)", Click = () => LoadRomState(machine) },
            new() { Label = "DISK-", Hint = "previous disk image in the folder (Ctrl+B)", Click = () => RomSwapDisk(machine, -1) },
            new() { Label = "DISK+", Hint = "next disk image in the folder (Ctrl+N)", Click = () => RomSwapDisk(machine, 1) },
            new() { Label = "TAPE", Hint = "tape PLAY / STOP (Ctrl+T)", Click = () => RomToggleTape(machine), On = () => machine.Tape.Active && machine.Tape.Play },
            new() { Label = "REWIND", Hint = "rewind the tape (Ctrl+R)", Click = () => RomRewindTape(machine) },
            new() { Label = "COPY", Hint = "copy the selection, or the screen (Ctrl+C)", Click = CopyToClipboard },
            new() { Label = "PASTE", Hint = "type the clipboard (Ctrl+V, middle click)", Click = PasteFromClipboard },
            new() { Label = "SHOT", Hint = "save a screenshot (F12)", Click = () => Screenshot(frame, $"c64-{DateTime.Now:yyyyMMdd-HHmmss}.bmp") },
            new() { Label = "FULL", Hint = "full screen (F11)", Click = () => ToggleFullscreen(window) },
        };
        SetUp(buttons, machine.ScreenRows, text => machine.Type(PasteText(text)));
        var joystick = (byte)0;
        long startedAt = Environment.TickCount64, titleAt = 0;
        string shownTitle = "";

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
                    case EventType.Mousemotion:
                        if (!UiMouseMoved(window, e.Motion.X, e.Motion.Y)) MouseMoved(window, e.Motion.X, e.Motion.Y, machine.Input);
                        break;
                    case EventType.Mousebuttondown or EventType.Mousebuttonup:
                        if (!UiMouseButton(window, e.Button)) MouseButton(e.Button, machine.Input);
                        break;
                    case EventType.Dropfile: RomDropFile(e.Drop.File, machine); break;
                    case EventType.Windowevent when e.Window.Event == (byte)WindowEventID.FocusLost:
                        machine.Input.ReleaseAllKeys();
                        _mouseButtons = 0;
                        joystick = 0;
                        break;
                }
            }

            if (TakePickedFile() is { } picked) RomOpenFile(picked, machine);

            if (_statusMessage is { } message)
            {
                _statusMessage = null;
                _statusUntil = Environment.TickCount64 + 3000;
                Sdl.SetWindowTitle(window, "C64 (ROM mode) - " + message);
            }
            else if (_statusUntil == 0 && Environment.TickCount64 - titleAt >= 250)
            {
                // the title shows the drive at work: its head position while the motor runs
                titleAt = Environment.TickCount64;
                string title = RomTitle(machine);
                if (title != shownTitle) { shownTitle = title; Sdl.SetWindowTitle(window, title); }
            }
            else if (_statusUntil != 0 && Environment.TickCount64 > _statusUntil)
            {
                _statusUntil = 0;
                shownTitle = "";
            }

            if (LightPen && _mouseButtons != 0) StrikeLightPen(window);
            _bus.Vic.Render(frame);
            if (snapshot != null && Environment.TickCount64 - startedAt >= 2000)
            {
                Screenshot(frame, snapshot);
                running = false;
            }

            Present(renderer, window, texture, frame);
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

    static string RomTitle(RomMachine machine)
    {
        string title = machine.Paused ? "C64 (ROM mode, paused) - " + machine.Registers() : _romWarp ? "C64 (ROM mode, warp)" : "C64 (ROM mode)";
        if (machine.Halted) return title + " - stopped: " + machine.HaltReason;
        for (int i = 0; i < machine.Drives.Length; i++)
        {
            var drive = machine.Drives[i].Mechanics;
            if (drive.MotorOn)
                title += $" - 1541{(machine.Drives.Length > 1 ? $" #{8 + i}" : "")}: track {drive.Track:0.#}{(drive.Writing ? ", writing" : "")}";
        }
        return title;
    }

    static void RomKeyDown(KeyboardEvent key, RomMachine machine, Window* window, uint[] frame, ref byte joystick)
    {
        var code = key.Keysym.Scancode;
        var mod = (Keymod)key.Keysym.Mod;
        bool shift = (mod & Keymod.Shift) != 0, control = (mod & Keymod.Ctrl) != 0, alt = (mod & Keymod.Alt) != 0;
        bool repeat = key.Repeat != 0;
        if (!(control && code == Scancode.ScancodeC) && code is not (Scancode.ScancodeLshift or Scancode.ScancodeRshift or Scancode.ScancodeLctrl or Scancode.ScancodeRctrl)) ClearSelection();
        if (code == Scancode.ScancodeF12 && control && !repeat) { ToolbarEnabled = !ToolbarEnabled; return; }

        switch (code)
        {
            case Scancode.ScancodeF9 when !repeat:
                _romWarp = !_romWarp;
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
            case Scancode.ScancodeScrolllock when !repeat:
                RomTogglePause(machine);
                return;
            case Scancode.ScancodePagedown:
                machine.Input.SetRestore(true);
                return;
            case Scancode.ScancodePause when !repeat:
                machine.Input.SetJoystick(_keyboardPort, 0);
                _keyboardPort = _keyboardPort == 2 ? 1 : 2;
                machine.Input.SetJoystick(_keyboardPort, joystick);
                _statusMessage = $"numpad = joystick {_keyboardPort}";
                return;
        }

        if (!repeat && control && code == Scancode.ScancodeO) { AskForFile(); return; }
        if (!repeat && control && code is Scancode.ScancodeN or Scancode.ScancodeB) { RomSwapDisk(machine, code == Scancode.ScancodeN ? 1 : -1); return; }
        if (!repeat && control && code == Scancode.ScancodeT) { RomToggleTape(machine); return; }
        if (!repeat && control && code == Scancode.ScancodeR) { RomRewindTape(machine); return; }
        if (!repeat && control && code == Scancode.ScancodeS) { SaveRomState(machine); return; }
        if (!repeat && control && code == Scancode.ScancodeL) { LoadRomState(machine); return; }
        if (!repeat && ((control && code == Scancode.ScancodeV) || (shift && code == Scancode.ScancodeInsert)))
        {
            PasteFromClipboard();
            return;
        }
        if (!repeat && control && code == Scancode.ScancodeC)
        {
            CopyToClipboard();
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

    /// <summary>Starts the first program of the disk in drive 8 from a fresh boot, as a user would: <c>LOAD"*",8,1</c> and RUN.</summary>
    static void StartFromDisk(RomMachine machine)
    {
        machine.Reset();
        machine.Type("LOAD\"*\",8,1\rRUN\r");
    }

    // ---------- what the keys and the toolbar both do ----------
    static void RomTogglePause(RomMachine machine)
    {
        machine.Paused = !machine.Paused;
        _statusMessage = machine.Paused ? "paused: " + machine.Registers() : "running";
    }

    static void RomSwapDisk(RomMachine machine, int step) =>
        machine.Post(() =>
        {
            try
            {
                string? path = machine.SwapDisk(step);
                _statusMessage = path != null ? "disk swapped: " + Path.GetFileName(path) : "no other disk image next to the mounted one";
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _statusMessage = e.Message; }
        });

    static void RomToggleTape(RomMachine machine) =>
        machine.Post(() =>
        {
            if (!machine.Tape.Active) { _statusMessage = "no tape mounted (--tape, or drop a .tap on the window)"; return; }
            if (machine.Tape.Play) machine.StopTape(); else machine.PlayTape();
            _statusMessage = machine.Tape.Play ? "tape: PLAY" : "tape: STOP";
        });

    static void RomRewindTape(RomMachine machine) =>
        machine.Post(() =>
        {
            if (!machine.Tape.Active) { _statusMessage = "no tape mounted"; return; }
            machine.Tape.Rewind();
            _statusMessage = "tape rewound";
        });

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
        if (path != null) RomOpenFile(path, machine);
    }

    /// <summary>Mounts or loads a file, whether it was dropped on the window or picked in the Open dialog.</summary>
    static void RomOpenFile(string path, RomMachine machine)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".d64":
                case ".g64":
                    {
                        machine.Post(() =>
                        {
                            try { machine.MountDiskFile(path); StartFromDisk(machine); _statusMessage = "started from " + Path.GetFileName(path); }
                            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _statusMessage = e.Message; }
                        });
                        break;
                    }
                case ".t64":
                case ".prg":
                    machine.Post(() =>
                    {
                        try { machine.MountDiskFile(path); StartFromDisk(machine); _statusMessage = "started from " + Path.GetFileName(path); }
                        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _statusMessage = e.Message; }
                    });
                    break;
                case ".crt":
                    machine.Post(() =>
                    {
                        try
                        {
                            var cartridge = C64Basic.Core.Machine.Cartridge.FromCrt(File.ReadAllBytes(path));
                            machine.InsertCartridge(cartridge);
                            _statusMessage = "cartridge: " + (cartridge.Name.Length > 0 ? cartridge.Name : Path.GetFileName(path)) + " (the machine was reset)";
                        }
                        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _statusMessage = e.Message; }
                    });
                    break;
                case ".tap":
                    machine.Post(() =>
                    {
                        try
                        {
                            machine.MountTapeFile(path);
                            machine.Reset();
                            machine.Type("LOAD\rRUN\r");
                            _statusMessage = "started from " + Path.GetFileName(path);
                        }
                        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _statusMessage = e.Message; }
                    });
                    break;
                case ".sav":
                    File.Copy(path, _romStateFile, overwrite: true);
                    LoadRomState(machine);
                    break;
                default:
                    _statusMessage = "ROM mode takes disk images (.d64, .g64, .t64, .prg), tapes (.tap), cartridges (.crt) and saved states (.sav)";
                    break;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
        }
    }

    // ---------- machine state ----------
    static void SaveRomState(RomMachine machine)
    {
        string file = _romStateFile;
        machine.Post(() =>
        {
            try { File.WriteAllBytes(file, machine.SaveState()); _statusMessage = $"state saved ({Path.GetFileName(file)})"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _statusMessage = "could not save: " + e.Message; }
        });
    }

    static void LoadRomState(RomMachine machine)
    {
        byte[] data;
        try { data = File.ReadAllBytes(_romStateFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _statusMessage = "no saved state (" + Path.GetFileName(_romStateFile) + ")";
            return;
        }
        machine.Post(() =>
        {
            try { machine.LoadState(data); _statusMessage = "state loaded"; }
            catch (Exception e) when (e is IOException or InvalidDataException) { _statusMessage = "could not load: " + e.Message; }
        });
    }
}
