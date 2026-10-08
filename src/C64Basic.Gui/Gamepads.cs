using C64Basic.Core.IO;
using Silk.NET.SDL;

namespace C64Basic.Gui;

/// <summary>
/// Game controllers as C64 joysticks: the first pad is on port 2 (where most games read it), the second on port 1. The D-pad and
/// the left stick give the directions, A, B, X and Y fire. Pads can be plugged in and out while the window is open.
/// </summary>
sealed unsafe class Gamepads
{
    sealed class Pad
    {
        public GameController* Handle;
        public int Instance;
        public bool Up, Down, Left, Right, Fire;
        public int AxisX, AxisY;

        public byte Bits
        {
            get
            {
                byte bits = JoystickMapping.FromAxes(AxisX, AxisY);
                if (Up) bits |= JoystickMapping.Up;
                if (Down) bits |= JoystickMapping.Down;
                if (Left) bits |= JoystickMapping.Left;
                if (Right) bits |= JoystickMapping.Right;
                if (Fire) bits |= JoystickMapping.Fire;
                return bits;
            }
        }
    }

    readonly Sdl _sdl;
    readonly ScreenConsole _console;
    readonly List<Pad> _pads = new();

    public Gamepads(Sdl sdl, ScreenConsole console)
    {
        _sdl = sdl;
        _console = console;
    }

    public int Count => _pads.Count;

    public void OpenAll()
    {
        int n = _sdl.NumJoysticks();
        for (int i = 0; i < n; i++) Open(i);
    }

    void Open(int index)
    {
        if (_sdl.IsGameController(index) != SdlBool.True) return;
        var handle = _sdl.GameControllerOpen(index);
        if (handle == null) return;
        int instance = _sdl.JoystickInstanceID(_sdl.GameControllerGetJoystick(handle));
        if (_pads.Any(p => p.Instance == instance)) { _sdl.GameControllerClose(handle); return; }
        _pads.Add(new Pad { Handle = handle, Instance = instance });
        Publish();
    }

    void Remove(int instance)
    {
        var pad = _pads.FirstOrDefault(p => p.Instance == instance);
        if (pad == null) return;
        _sdl.GameControllerClose(pad.Handle);
        _pads.Remove(pad);
        Publish();
    }

    public void Handle(in Event e)
    {
        switch ((EventType)e.Type)
        {
            case EventType.Controllerdeviceadded: Open(e.Cdevice.Which); break;
            case EventType.Controllerdeviceremoved: Remove(e.Cdevice.Which); break;
            case EventType.Controllerbuttondown: Button(e.Cbutton.Which, (GameControllerButton)e.Cbutton.Button, true); break;
            case EventType.Controllerbuttonup: Button(e.Cbutton.Which, (GameControllerButton)e.Cbutton.Button, false); break;
            case EventType.Controlleraxismotion: Axis(e.Caxis.Which, (GameControllerAxis)e.Caxis.Axis, e.Caxis.Value); break;
        }
    }

    void Button(int instance, GameControllerButton button, bool down)
    {
        var pad = _pads.FirstOrDefault(p => p.Instance == instance);
        if (pad == null) return;
        switch (button)
        {
            case GameControllerButton.ControllerButtonDpadUp: pad.Up = down; break;
            case GameControllerButton.ControllerButtonDpadDown: pad.Down = down; break;
            case GameControllerButton.ControllerButtonDpadLeft: pad.Left = down; break;
            case GameControllerButton.ControllerButtonDpadRight: pad.Right = down; break;
            case GameControllerButton.ControllerButtonA:
            case GameControllerButton.ControllerButtonB:
            case GameControllerButton.ControllerButtonX:
            case GameControllerButton.ControllerButtonY: pad.Fire = down; break;
            default: return;
        }
        Publish();
    }

    void Axis(int instance, GameControllerAxis axis, int value)
    {
        var pad = _pads.FirstOrDefault(p => p.Instance == instance);
        if (pad == null) return;
        if (axis == GameControllerAxis.ControllerAxisLeftx) pad.AxisX = value;
        else if (axis == GameControllerAxis.ControllerAxisLefty) pad.AxisY = value;
        else return;
        Publish();
    }

    /// <summary>Tells the console what each pad holds; pads beyond the second have no port.</summary>
    void Publish()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            // clear both ports for this source, then set the pad's own
            _console.SetJoystick(1, 0, slot + 1);
            _console.SetJoystick(2, 0, slot + 1);
            if (slot >= _pads.Count || slot > 1) continue;
            _console.SetJoystick(slot == 0 ? 2 : 1, _pads[slot].Bits, slot + 1);
        }
    }

    public void CloseAll()
    {
        foreach (var pad in _pads) _sdl.GameControllerClose(pad.Handle);
        _pads.Clear();
    }
}
