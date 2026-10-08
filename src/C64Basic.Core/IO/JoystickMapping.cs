namespace C64Basic.Core.IO;

/// <summary>Joystick state as the CIA reads it (a set bit means pressed) and ways to build it from host input.</summary>
public static class JoystickMapping
{
    public const byte Up = 1, Down = 2, Left = 4, Right = 8, Fire = 16;

    /// <summary>Stick axes in the SDL range (-32768..32767, y negative is up) to directions, ignoring small movements.</summary>
    public static byte FromAxes(int x, int y, int deadZone = 12000)
    {
        byte bits = 0;
        if (x < -deadZone) bits |= Left;
        if (x > deadZone) bits |= Right;
        if (y < -deadZone) bits |= Up;
        if (y > deadZone) bits |= Down;
        return bits;
    }

    /// <summary>A 1541-era joystick has no diagonals that conflict: opposite directions cancel each other out.</summary>
    public static byte Normalize(byte bits)
    {
        if ((bits & (Up | Down)) == (Up | Down)) bits &= unchecked((byte)~(Up | Down));
        if ((bits & (Left | Right)) == (Left | Right)) bits &= unchecked((byte)~(Left | Right));
        return bits;
    }
}
