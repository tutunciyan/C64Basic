using C64Basic.Core.Rom;

namespace C64Basic.Tests;

public class RomSetTests
{
    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "c64roms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Crc32MatchesTheStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, RomSet.Crc32("123456789"u8));

    [Fact]
    public void FindsTheRomsByNameOrBySizeAndPrefix()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "basic-901226-01.bin"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(dir, "KERNAL.ROM"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(dir, "1541-II.rom"), new byte[16384]);
            File.WriteAllBytes(Path.Combine(dir, "chargen.bin"), new byte[4096]);
            var set = RomSet.Find(dir);
            Assert.Equal(8192, set.Basic.Length);
            Assert.Equal(8192, set.Kernal.Length);
            Assert.Equal(16384, set.Dos.Length);
            Assert.Equal(4096, set.Chargen!.Length);
            Assert.Equal(4, set.Notes.Count);
            Assert.All(set.Notes, n => Assert.Contains("unrecognised", n));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheCharacterRomIsOptional()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "basic.bin"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(dir, "kernal.bin"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(dir, "dos1541.bin"), new byte[16384]);
            Assert.Null(RomSet.Find(dir).Chargen);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingRomsAreNamed()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "basic.bin"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(dir, "kernal.bin"), new byte[1000]);     // wrong size
            var e = Assert.Throws<FileNotFoundException>(() => RomSet.Find(dir));
            Assert.Contains("KERNAL", e.Message);
            Assert.Contains("1541 DOS", e.Message);
            Assert.DoesNotContain("BASIC", e.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AMissingFolderIsReported() =>
        Assert.Throws<DirectoryNotFoundException>(() => RomSet.Find(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void WrongSizesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new RomSet(new byte[100], new byte[8192], new byte[16384]));
        Assert.Throws<ArgumentException>(() => new RomSet(new byte[8192], new byte[8192], new byte[8192]));
    }
}
