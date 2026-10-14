using System.Runtime.InteropServices;
using C64Basic.Core.Disk;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using Silk.NET.Core.Contexts;
using Silk.NET.SDL;

namespace C64Basic.Gui;

/// <summary>The window: SDL events in, the VIC-II frame and SID audio out. The machine runs on its own thread.</summary>
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

    /// <summary>A message from the machine thread for the title bar (SDL calls stay on the main thread).</summary>
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
        float gain = _volumeGain;
        if (gain < 0.999f)
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(samples[i] * gain);
    }

    // ---------- the mouse is a paddle ----------
    // Its position is the paddle on the port the numpad drives (POTX/POTY at $D419/$D41A once $DC00 bits 7-6 select that port);
    // the left button is fire A and the right button fire B (the joystick's left and right bits). It has its own input source,
    // so it never disturbs the keyboard or a game controller.
    const int MouseSource = 6;
    static byte _mouseButtons;

    static int _mouseX, _mouseY;

    /// <summary>Where the pointer is in the picture, as the beam sees it: sprite X (frame x - 8) and the raster line (frame row + 15).</summary>
    static bool BeamAt(out int x, out int line)
    {
        bool inside = FramePoint(_mouseX, _mouseY, out int fx, out int fy);
        x = fx - 8;
        line = fy + 15;
        return inside;
    }

    /// <summary>A light pen strike for the frame being drawn while the button is down: the first one per frame latches.</summary>
    static void StrikeLightPen()
    {
        if (BeamAt(out int x, out int line)) _bus.Vic.LightPen(x, line);
    }

    static void MouseMoved(IGameInput console)
    {
        if (LightPen) return;
        FramePoint(_mouseX, _mouseY, out int fx, out int fy, clamp: true);
        console.SetPaddle(_keyboardPort, 0, fx * 255 / (Vic2.FrameWidth - 1));
        console.SetPaddle(_keyboardPort, 1, fy * 255 / (Vic2.FrameHeight - 1));
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

    // ---------- window ----------
    static bool IsFullscreen(Window* window) => (Sdl.GetWindowFlags(window) & (uint)WindowFlags.FullscreenDesktop) != 0;

    static void ToggleFullscreen(Window* window)
    {
        bool full = (Sdl.GetWindowFlags(window) & (uint)WindowFlags.FullscreenDesktop) != 0;
        Sdl.SetWindowFullscreen(window, full ? 0 : (uint)WindowFlags.FullscreenDesktop);
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
