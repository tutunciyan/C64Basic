using C64Basic.Core.Machine;

namespace C64Basic.Core.IO;

/// <summary>
/// What the host holds down: the keyboard matrix, joysticks, paddles and the RESTORE key, for the real keyboard scan of ROM mode
/// to read through CIA 1. Thread-safe enough for a host that sets keys on one thread while the machine runs on another.
/// </summary>
public sealed class InputState : IInputDevice
{
    public const int JoystickSources = 8;

    readonly bool[] _matrix = new bool[64];
    readonly byte[,] _joystick = new byte[JoystickSources, 3];
    readonly byte[,] _paddle = new byte[3, 2];
    volatile bool _restore;

    /// <summary>Presses or releases the key with the KERNAL index <c>column * 8 + row</c> (0 = DEL, 1 = RETURN, 60 = SPACE, 63 = RUN/STOP).</summary>
    public void SetKey(int index, bool down)
    {
        if (index is >= 0 and < 64) _matrix[index] = down;
    }

    public bool IsDown(int index) => index is >= 0 and < 64 && _matrix[index];

    public void ReleaseAllKeys()
    {
        Array.Clear(_matrix);
        Array.Clear(_joystick);
    }

    public byte KeyColumn(int column)
    {
        int rows = 0;
        for (int row = 0; row < 8; row++) if (_matrix[column * 8 + row]) rows |= 1 << row;
        return (byte)rows;
    }

    /// <summary>Sets what one input source (0 = the keyboard, 1-4 = gamepads) holds on a joystick port (1 or 2).</summary>
    public void SetJoystick(int port, byte bits, int source = 0)
    {
        if (port is < 1 or > 2 || source is < 0 or >= JoystickSources) return;
        _joystick[source, port] = JoystickMapping.Normalize(bits);
    }

    public byte Joystick(int port)
    {
        if (port is < 1 or > 2) return 0;
        byte bits = 0;
        for (int source = 0; source < JoystickSources; source++) bits |= _joystick[source, port];
        return JoystickMapping.Normalize(bits);
    }

    /// <summary>Sets a paddle position (0-255) for game port 1 or 2; axis 0 is POTX, 1 is POTY.</summary>
    public void SetPaddle(int port, int axis, int value)
    {
        if (port is < 1 or > 2 || axis is < 0 or > 1) return;
        _paddle[port, axis] = (byte)Math.Clamp(value, 0, 255);
    }

    public byte Paddle(int port, int axis) => port is < 1 or > 2 || axis is < 0 or > 1 ? (byte)0 : _paddle[port, axis];

    public bool Restore => _restore;

    public void SetRestore(bool down) => _restore = down;
}
