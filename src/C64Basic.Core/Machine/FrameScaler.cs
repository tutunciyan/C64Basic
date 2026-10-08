namespace C64Basic.Core.Machine;

/// <summary>Shrinking a VIC-II frame to fit a character terminal, where one cell shows two stacked pixels (an upper half block).</summary>
public static class FrameScaler
{
    /// <summary>
    /// The size, in cells and in pixel rows, to show a frame of the given size in a terminal. The picture is never enlarged, keeps its
    /// aspect (a cell is twice as tall as it is wide, so two pixel rows fill one cell row) and has an even number of pixel rows.
    /// </summary>
    public static (int Columns, int PixelRows) Fit(int terminalColumns, int terminalRows, int frameWidth, int frameHeight)
    {
        if (terminalColumns < 1 || terminalRows < 1) return (1, 2);
        double scale = Math.Min(1.0, Math.Min(terminalColumns / (double)frameWidth, terminalRows * 2 / (double)frameHeight));
        int columns = Math.Max(1, (int)Math.Floor(frameWidth * scale));
        int rows = Math.Max(2, (int)Math.Floor(frameHeight * scale) / 2 * 2);
        return (columns, rows);
    }

    /// <summary>Box-filters <paramref name="source"/> (ARGB) down to <paramref name="destination"/>: every output pixel is the average of the pixels it covers.</summary>
    public static void Scale(ReadOnlySpan<uint> source, int sourceWidth, int sourceHeight,
                             Span<uint> destination, int destinationWidth, int destinationHeight)
    {
        if (sourceWidth < 1 || sourceHeight < 1 || destinationWidth < 1 || destinationHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "sizes must be positive");
        if (source.Length < sourceWidth * sourceHeight) throw new ArgumentException("source is too small", nameof(source));
        if (destination.Length < destinationWidth * destinationHeight) throw new ArgumentException("destination is too small", nameof(destination));

        for (int dy = 0; dy < destinationHeight; dy++)
        {
            int y0 = (int)((long)dy * sourceHeight / destinationHeight);
            int y1 = Math.Max(y0 + 1, (int)(((long)(dy + 1) * sourceHeight + destinationHeight - 1) / destinationHeight));
            y1 = Math.Min(y1, sourceHeight);
            for (int dx = 0; dx < destinationWidth; dx++)
            {
                int x0 = (int)((long)dx * sourceWidth / destinationWidth);
                int x1 = Math.Max(x0 + 1, (int)(((long)(dx + 1) * sourceWidth + destinationWidth - 1) / destinationWidth));
                x1 = Math.Min(x1, sourceWidth);

                long r = 0, g = 0, b = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        uint p = source[y * sourceWidth + x];
                        r += p >> 16 & 0xFF; g += p >> 8 & 0xFF; b += p & 0xFF;
                        count++;
                    }
                }
                destination[dy * destinationWidth + dx] =
                    0xFF000000u | (uint)(r / count) << 16 | (uint)(g / count) << 8 | (uint)(b / count);
            }
        }
    }
}
