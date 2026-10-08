namespace C64Basic.Core.Runtime;

/// <summary>
/// The C64 character generator ROM (uppercase/graphics set) as seen at 53248-57343 after
/// <c>POKE 1,PEEK(1) AND 251</c>. Screen codes 0-63 are the real glyphs; 128-255 are their inverses.
/// Of the graphics at 64-127 only the shapes that have a PETSCII equivalent in <see cref="Petscii"/> and
/// are simple to draw are included; the rest read as blank.
/// </summary>
public static class CharRom
{
    public const int Start = 53248, Length = 4096;

    static readonly byte[] Data = Build();

    public static byte Read(int address) => Data[address - Start];

    static byte[] Build()
    {
        string[] rows =
        {
            "3C666E6E60623C00", "183C667E66666600", "7C66667C66667C00", "3C66606060663C00", "786C6666666C7800",
            "7E60607860607E00", "7E60607860606000", "3C66606E66663C00", "666666 7E66666600", "3C18181818183C00",
            "1E0C0C0C0C6C3800", "666C78707 86C6600", "6060606060607E00", "63777F6B63636300", "66767E7E6E666600",
            "3C66666666663C00", "7C66667C60606000", "3C666666663C0E00", "7C66667C786C6600", "3C66603C06663C00",
            "7E18181818181800", "6666666666663C00", "66666666663C1800", "6363636B7F776300", "66663C183C666600",
            "6666663C18181800", "7E060C1830607E00", "3C30303030303C00", "0E1830 7C3062FC00", "3C0C0C0C0C0C3C00",
            "00183C7E18181818", "0010307F7F301000", "0000000000000000", "1818181800001800", "6666660000000000",
            "6666FF66FF666600", "183E603C067C1800", "62660C1830664600", "3C663C386766 3F00", "060C180000000000",
            "0C18303030180C00", "30180C0C0C183000", "00663CFF3C660000", "0018187E18180000", "0000000000181830",
            "0000007E00000000", "0000000000181800", "0003060C18306000", "3C666E7666663C00", "1818381818187E00",
            "3C66060C30607E00", "3C66061C06663C00", "060E1E667F060600", "7E607C0606663C00", "3C66607C66663C00",
            "7E660C1818181800", "3C66663C66663C00", "3C66663E06663C00", "0000180000180000", "0000180000181830",
            "0E18306030180E00", "00007E007E000000", "701 80C060C187000", "3C66060C18001800",
        };
        var data = new byte[Length];
        for (int code = 0; code < 64; code++)
        {
            string hex = rows[code].Replace(" ", "");
            for (int r = 0; r < 8; r++) data[code * 8 + r] = Convert.ToByte(hex.Substring(r * 2, 2), 16);
        }

        // a few graphics (screen codes 64-95)
        Put(data, 64, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00); // horizontal line
        Put(data, 65, 0x08, 0x1C, 0x3E, 0x7F, 0x7F, 0x1C, 0x3E, 0x00); // spade
        Put(data, 66, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18); // vertical line
        Put(data, 77, 0xC0, 0xE0, 0x70, 0x38, 0x1C, 0x0E, 0x07, 0x03); // diagonal \
        Put(data, 78, 0x03, 0x07, 0x0E, 0x1C, 0x38, 0x70, 0xE0, 0xC0); // diagonal /
        Put(data, 81, 0x00, 0x3C, 0x7E, 0x7E, 0x7E, 0x7E, 0x3C, 0x00); // ball
        Put(data, 86, 0xC3, 0xE7, 0x7E, 0x3C, 0x3C, 0x7E, 0xE7, 0xC3); // X
        Put(data, 87, 0x00, 0x3C, 0x42, 0x42, 0x42, 0x42, 0x3C, 0x00); // ring
        Put(data, 91, 0x18, 0x18, 0x18, 0xFF, 0xFF, 0x18, 0x18, 0x18); // cross

        // the second half of each 128-character set is the first half inverted
        for (int i = 0; i < 1024; i++) data[1024 + i] = (byte)~data[i];
        Array.Copy(data, 0, data, 2048, 2048); // the lower-case set is not modelled: same shapes
        return data;
    }

    static void Put(byte[] data, int code, params byte[] rows) => Array.Copy(rows, 0, data, code * 8, 8);
}
