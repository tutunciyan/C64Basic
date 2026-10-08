namespace C64Basic.Core.Machine;

/// <summary>What the KERNAL's keyboard scan leaves in zero page: the key held (197) and the shift flags (653).</summary>
public static class Keyboard
{
    public const int CurrentKey = 197, ShiftFlags = 653, NoKey = 64;

    // KERNAL key indexes (column * 8 + row) of the modifier keys
    const int LeftShift = 15, RightShift = 52, Commodore = 61, Control = 58;

    public static byte Read(IInputDevice input, int address)
    {
        int key = NoKey, flags = 0;
        for (int column = 0; column < 8; column++)
        {
            int rows = input.KeyColumn(column);
            for (int row = 0; row < 8; row++)
            {
                if ((rows >> row & 1) == 0) continue;
                switch (column * 8 + row)
                {
                    case LeftShift: case RightShift: flags |= 1; break;
                    case Commodore: flags |= 2; break;
                    case Control: flags |= 4; break;
                    case var k: if (key == NoKey) key = k; break;
                }
            }
        }
        return (byte)(address == CurrentKey ? key : flags);
    }
}
