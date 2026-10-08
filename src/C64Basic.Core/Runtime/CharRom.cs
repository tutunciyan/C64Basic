namespace C64Basic.Core.Runtime;

/// <summary>
/// The character generator ROM as the VIC-II and the CPU see it (4096 bytes: two sets of 256 glyphs, 8 bytes each).
/// The real ROM is copyrighted and cannot be shipped, so the built-in set is drawn here: letters, digits and
/// punctuation in the C64's style, hand-drawn graphics for screen codes 64-127, a lower-case set, and the reversed
/// half of each set. <see cref="Bus.LoadCharacterRom"/> replaces it with a real dump (the GUI's <c>--chargen</c>).
/// </summary>
public static class CharRom
{
    public const int Start = 53248, Length = 4096;

    /// <summary>First byte of the lower-case set inside the ROM (selected by bit 1 of $D018).</summary>
    public const int LowerSet = 2048;

    /// <summary>A byte of the built-in ROM at its CPU address (53248-57343).</summary>
    public static byte Read(int address) => Data[address - Start];

    /// <summary>A fresh copy of the built-in ROM.</summary>
    public static byte[] CreateDefault() => (byte[])Data.Clone();

    static byte[] Build()
    {
        var data = new byte[Length];

        // screen codes 0-63: @, A-Z, [ pound ] up-arrow left-arrow, space, punctuation, digits
        for (int code = 0; code < 64; code++) Put(data, code, Upper[code]);

        // 64-127: graphics, identical in both sets
        for (int i = 0; i < Graphics.Length; i++) Put(data, 64 + i, Graphics[i]);

        // the second half of each 128-glyph set is the first half inverted
        for (int i = 0; i < 1024; i++) data[1024 + i] = (byte)~data[i];

        // the lower-case set: lower-case letters at 1-26, capitals at 65-90, the rest as in the first set
        Array.Copy(data, 0, data, LowerSet, 1024);
        for (int i = 0; i < 26; i++)
        {
            Put(data, LowerSet / 8 + 1 + i, Lowercase[i]);
            Array.Copy(data, (1 + i) * 8, data, LowerSet + (65 + i) * 8, 8);
        }
        for (int i = 0; i < 1024; i++) data[LowerSet + 1024 + i] = (byte)~data[LowerSet + i];
        return data;
    }

    static void Put(byte[] data, int code, string hex)
    {
        for (int r = 0; r < 8; r++) data[code * 8 + r] = Convert.ToByte(hex.Substring(r * 2, 2), 16);
    }

    // screen codes 0-63
    static readonly string[] Upper =
    {
        "3C666E6E60623C00", "183C667E66666600", "7C66667C66667C00", "3C66606060663C00", "786C6666666C7800",
        "7E60607860607E00", "7E60607860606000", "3C66606E66663C00", "66667E6666666600", "3C18181818183C00",
        "1E0C0C0C0C6C3800", "666C7870786C6600", "6060606060607E00", "63777F6B63636300", "66767E7E6E666600",
        "3C66666666663C00", "7C66667C60606000", "3C666666663C0E00", "7C66667C786C6600", "3C66603C06663C00",
        "7E18181818181800", "6666666666663C00", "66666666663C1800", "6363636B7F776300", "66663C183C666600",
        "6666663C18181800", "7E060C1830607E00", "3C30303030303C00", "0E18307C3062FC00", "3C0C0C0C0C0C3C00",
        "00183C7E18181818", "0010307F7F301000", "0000000000000000", "1818181800001800", "6666660000000000",
        "6666FF66FF666600", "183E603C067C1800", "62660C1830664600", "3C663C3867663F00", "060C180000000000",
        "0C18303030180C00", "30180C0C0C183000", "00663CFF3C660000", "0018187E18180000", "0000000000181830",
        "0000007E00000000", "0000000000181800", "0003060C18306000", "3C666E7666663C00", "1818381818187E00",
        "3C66060C30607E00", "3C66061C06663C00", "060E1E667F060600", "7E607C0606663C00", "3C66607C66663C00",
        "7E660C1818181800", "3C66663C66663C00", "3C66663E06663C00", "0000180000180000", "0000180000181830",
        "0E18306030180E00", "00007E007E000000", "70180C060C187000", "3C66060C18001800",
    };

    // screen codes 64-127 in the order of the PETSCII graphics table: lines, corners, blocks, suits, checkerboard
    static readonly string[] Graphics =
    {
        "000000FFFF000000", // 64  horizontal line
        "081C3E7F7F36081C", // 65  spade
        "1818181818181818", // 66  vertical line
        "0000FF0000000000", // 67  line, row 2
        "00FF000000000000", // 68  line, row 1
        "0000000000FF0000", // 69  line, row 5
        "3030303030303030", // 70  vertical, left of centre
        "0C0C0C0C0C0C0C0C", // 71  vertical, right of centre
        "000000F8F8181818", // 72  corner: left and down
        "000000E0F0381818", // 73  rounded corner: left and down
        "18181C0E07000000", // 74  rounded corner: up and right
        "18183870E0000000", // 75  rounded corner: up and left
        "000000000F0F0F0F", // 76  lower right quadrant
        "C0E070381C0E0703", // 77  diagonal \
        "03070E1C3870E0C0", // 78  diagonal /
        "F0F0F0F000000000", // 79  upper left quadrant
        "0F0F0F0F00000000", // 80  upper right quadrant
        "003C7E7E7E7E3C00", // 81  ball
        "000000000000FF00", // 82  line, row 6
        "367F7F7F3E1C0800", // 83  heart
        "0606060606060606", // 84  vertical, far right
        "000000070F1C1818", // 85  rounded corner: right and down
        "C3E77E3C3C7EE7C3", // 86  cross
        "003C424242423C00", // 87  ring
        "18185AFFFF5A183C", // 88  club
        "6060606060606060", // 89  vertical, far left
        "081C3E7F3E1C0800", // 90  diamond
        "181818FFFF181818", // 91  crossing lines
        "AA55AA55AA55AA55", // 92  checkerboard
        "C0C0C0C0C0C0C0C0", // 93  vertical, edge
        "007F363636366300", // 94  pi
        "FF7F3F1F0F070301", // 95  upper right triangle
        "0000000000000000", // 96  space
        "F0F0F0F0F0F0F0F0", // 97  left half
        "00000000FFFFFFFF", // 98  lower half
        "FF00000000000000", // 99  top line
        "00000000000000FF", // 100 bottom line
        "8080808080808080", // 101 left edge
        "AA55AA55AA55AA55", // 102 checkerboard
        "0303030303030303", // 103 right edge
        "FFFEFCF8F0E0C080", // 104 upper left triangle
        "FF7F3F1F0F070301", // 105 upper right triangle
        "1818181F1F181818", // 106 tee: right
        "000000000F0F0F0F", // 107 lower right quadrant
        "1818181F1F000000", // 108 corner: up and right
        "000000F8F8181818", // 109 corner: left and down
        "000000000000FFFF", // 110 lower quarter
        "0000001F1F181818", // 111 corner: right and down
        "181818FFFF000000", // 112 tee: up
        "000000FFFF181818", // 113 tee: down
        "181818F8F8181818", // 114 tee: left
        "C0C0C0C0C0C0C0C0", // 115 left quarter
        "E0E0E0E0E0E0E0E0", // 116 left three eighths
        "0707070707070707", // 117 right three eighths
        "FFFF000000000000", // 118 top quarter
        "FFFFFF0000000000", // 119 top three eighths
        "0000000000FFFFFF", // 120 lower three eighths
        "000103068CD87020", // 121 check mark
        "00000000F0F0F0F0", // 122 lower left quadrant
        "0F0F0F0F00000000", // 123 upper right quadrant
        "181818F8F8000000", // 124 corner: up and left
        "F0F0F0F000000000", // 125 upper left quadrant
        "F0F0F0F00F0F0F0F", // 126 diagonal quadrants
        "0F0F0F0FF0F0F0F0", // 127 diagonal quadrants
    };

    // lower-case letters a-z (screen codes 1-26 of the second set)
    static readonly string[] Lowercase =
    {
        "00003C063E663E00", "0060607C66667C00", "00003C6060603C00", "0006063E66663E00", "00003C667E603C00",
        "0E183E1818181800", "00003E66663E067C", "0060607C66666600", "0018003818183C00", "000600060606063C",
        "006060666C786C66", "0038181818183C00", "0000667F7F6B6300", "00007C6666666600", "00003C6666663C00",
        "00007C66667C6060", "00003E66663E0606", "00007C6660606000", "00003E603C067C00", "00187E1818180E00",
        "0000666666663E00", "00006666663C1800", "0000636B7F3E3600", "0000663C183C6600", "00006666663E0C78",
        "00007E0C18307E00",
    };

    // declared last: the glyph tables above must exist before Build runs
    static readonly byte[] Data = Build();
}
