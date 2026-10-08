using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class FrameScalerTests
{
    static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;

    [Fact]
    public void ScalingToTheSameSizeCopies()
    {
        var src = new uint[] { Rgb(1, 2, 3), Rgb(4, 5, 6), Rgb(7, 8, 9), Rgb(10, 11, 12) };
        var dst = new uint[4];
        FrameScaler.Scale(src, 2, 2, dst, 2, 2);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void ShrinkingAveragesTheCoveredPixels()
    {
        var src = new uint[] { Rgb(0, 0, 0), Rgb(100, 200, 40), Rgb(100, 0, 0), Rgb(0, 200, 120) };
        var dst = new uint[1];
        FrameScaler.Scale(src, 2, 2, dst, 1, 1);
        Assert.Equal(Rgb(50, 100, 40), dst[0]);
    }

    [Fact]
    public void ASolidColourStaysSolid()
    {
        var src = Enumerable.Repeat(Rgb(10, 20, 30), 384 * 272).ToArray();
        var dst = new uint[100 * 60];
        FrameScaler.Scale(src, 384, 272, dst, 100, 60);
        Assert.All(dst, p => Assert.Equal(Rgb(10, 20, 30), p));
    }

    [Fact]
    public void AnOddRatioStillCoversEveryPixel()
    {
        // 5 -> 2: the first output pixel covers columns 0-2, the second columns 2-4 (the middle one is shared)
        var src = new uint[] { Rgb(0, 0, 0), Rgb(0, 0, 0), Rgb(60, 60, 60), Rgb(120, 120, 120), Rgb(120, 120, 120) };
        var dst = new uint[2];
        FrameScaler.Scale(src, 5, 1, dst, 2, 1);
        Assert.True((dst[0] & 0xFF) < (dst[1] & 0xFF));
        Assert.Equal(0xFF000000u, dst[0] & 0xFF000000u);
    }

    [Fact]
    public void ABrightPixelStillShowsAfterShrinking()
    {
        var src = new uint[384 * 272];
        Array.Fill(src, Rgb(0, 0, 0));
        for (int y = 100; y < 121; y++) for (int x = 150; x < 174; x++) src[y * 384 + x] = Rgb(255, 255, 255);   // a sprite
        var dst = new uint[96 * 68];
        FrameScaler.Scale(src, 384, 272, dst, 96, 68);
        Assert.Contains(dst, p => (p & 0xFF) > 200);
        Assert.Contains(dst, p => (p & 0xFF) == 0);
    }

    [Fact]
    public void UpscalingRepeatsPixels()
    {
        var src = new uint[] { Rgb(255, 0, 0), Rgb(0, 0, 255) };
        var dst = new uint[4];
        FrameScaler.Scale(src, 2, 1, dst, 4, 1);
        Assert.Equal(new[] { Rgb(255, 0, 0), Rgb(255, 0, 0), Rgb(0, 0, 255), Rgb(0, 0, 255) }, dst);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void EmptyTerminalsGetAMinimalPicture(int cols, int rows) =>
        Assert.Equal((1, 2), FrameScaler.Fit(cols, rows, 384, 272));

    [Fact]
    public void BigTerminalsShowTheFrameAtItsOwnSizeNotLarger()
    {
        Assert.Equal((384, 272), FrameScaler.Fit(500, 300, 384, 272));
    }

    [Fact]
    public void NarrowTerminalsShrinkByWidthAndKeepTheAspect()
    {
        var (cols, rows) = FrameScaler.Fit(96, 200, 384, 272);
        Assert.Equal(96, cols);
        Assert.Equal(68, rows);
    }

    [Fact]
    public void ShortTerminalsShrinkByHeight()
    {
        var (cols, rows) = FrameScaler.Fit(400, 34, 384, 272);       // 34 cell rows = 68 pixel rows
        Assert.Equal(68, rows);
        Assert.Equal(96, cols);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(200, 50)]
    public void ThePixelRowCountIsEvenAndFits(int cols, int rows)
    {
        var (c, r) = FrameScaler.Fit(cols, rows, 384, 272);
        Assert.Equal(0, r % 2);
        Assert.InRange(c, 1, cols);
        Assert.InRange(r / 2, 1, rows);
    }

    [Fact]
    public void BadSizesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameScaler.Scale(new uint[1], 0, 1, new uint[1], 1, 1));
        Assert.Throws<ArgumentException>(() => FrameScaler.Scale(new uint[1], 2, 2, new uint[4], 2, 2));
        Assert.Throws<ArgumentException>(() => FrameScaler.Scale(new uint[4], 2, 2, new uint[1], 2, 2));
    }
}
