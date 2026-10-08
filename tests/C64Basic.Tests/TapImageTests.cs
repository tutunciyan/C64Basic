using System.Text;
using C64Basic.Core.Disk;

namespace C64Basic.Tests;

public class TapImageTests
{
    static byte[] Prg(int address, params byte[] data) =>
        new byte[] { (byte)address, (byte)(address >> 8) }.Concat(data).ToArray();

    static byte[] Bytes(int count, int seed = 1) => Enumerable.Range(0, count).Select(i => (byte)(i * 13 + seed)).ToArray();

    static TapImage Reopen(TapImage tape) => new(tape.ToArray());

    [Fact]
    public void AProgramSurvivesAWriteAndReload()
    {
        var tape = new TapImage();
        var program = Prg(0x0801, Bytes(300));
        tape.Write("GAME", FileType.Prg, program, false);
        var back = Reopen(tape).Read("GAME");
        Assert.Equal("GAME", back.Name);
        Assert.Equal(program, back.Data);
    }

    [Fact]
    public void MachineCodeKeepsItsLoadAddress()
    {
        var tape = new TapImage();
        var code = Prg(0xC000, 0xA9, 0x01, 0x60);
        tape.Write("ML", FileType.Prg, code, false);
        Assert.Equal(code, Reopen(tape).Read("ML").Data);
    }

    [Fact]
    public void SeveralProgramsAreKeptInOrder()
    {
        var tape = new TapImage();
        tape.Write("ONE", FileType.Prg, Prg(0x0801, 1, 2, 3), false);
        tape.Write("TWO", FileType.Prg, Prg(0x0801, 4, 5), false);
        tape.Write("THREE", FileType.Prg, Prg(0xC000, 6), false);
        var again = Reopen(tape);
        Assert.Equal(new[] { "ONE", "TWO", "THREE" }, again.Directory().Select(d => d.Name));
        Assert.Equal(Prg(0x0801, 4, 5), again.Read("TWO").Data);
        Assert.Equal("ONE", again.Read("").Name);                 // no name: the next program
    }

    [Fact]
    public void LargeProgramsRoundTrip()
    {
        var tape = new TapImage();
        var program = Prg(0x0801, Bytes(20000, 7));
        tape.Write("BIG", FileType.Prg, program, false);
        Assert.Equal(program, Reopen(tape).Read("BIG").Data);
    }

    [Fact]
    public void EmptyProgramsRoundTrip()
    {
        var tape = new TapImage();
        tape.Write("EMPTY", FileType.Prg, Prg(0x0801), false);
        Assert.Equal(Prg(0x0801), Reopen(tape).Read("EMPTY").Data);
    }

    [Fact]
    public void TheImageHasTheTapHeader()
    {
        var tape = new TapImage();
        tape.Write("X", FileType.Prg, Prg(0x0801, 1), false);
        var image = tape.ToArray();
        Assert.Equal("C64-TAPE-RAW", Encoding.ASCII.GetString(image, 0, 12));
        Assert.Equal(1, image[12]);                                   // version
        Assert.Equal(image.Length - 20, BitConverter.ToInt32(image, 16));
    }

    [Fact]
    public void PulsesUseTheKernalLengths()
    {
        var tape = new TapImage();
        tape.Write("X", FileType.Prg, Prg(0x0801, 1), false);
        var pulses = tape.ToArray()[20..];
        Assert.Equal(0x30, pulses[0]);                                // the leader is short pulses
        Assert.Equal(0x30, pulses[4000]);
        int firstMarker = Array.FindIndex(pulses, b => b == 0x56);
        Assert.Equal(0x42, pulses[firstMarker + 1]);                  // a byte marker is long, medium
        Assert.Contains((byte)0x42, pulses);
    }

    [Fact]
    public void TheHeaderBlockCarriesTheNameAndAddresses()
    {
        var tape = new TapImage();
        tape.Write("HELLO", FileType.Prg, Prg(0x0801, 1, 2, 3, 4), false);
        var again = Reopen(tape);
        var entry = again.Directory().Single();
        Assert.Equal("HELLO", entry.Name);
        Assert.Equal(1, entry.Blocks);
    }

    [Fact]
    public void ABadFirstCopyIsReplacedByTheRepeat()
    {
        var tape = new TapImage();
        tape.Write("SAFE", FileType.Prg, Prg(0x0801, Bytes(100)), false);
        var image = tape.ToArray();

        // damage a pulse in the middle of the first copy of the data block, long before the repeat
        int damage = 20 + 4096 + 20 * 9 * 2 + 20 * 192 * 2 + 200;     // inside the first header copy's data bytes
        image[damage] = 0x56;
        var again = new TapImage(image);
        Assert.Equal(Prg(0x0801, Bytes(100)), again.Read("SAFE").Data);
    }

    [Fact]
    public void NoiseBeforeTheLeaderIsIgnored()
    {
        var tape = new TapImage();
        tape.Write("QUIET", FileType.Prg, Prg(0x0801, 9, 9), false);
        var noisy = new List<byte> { 0x30, 0x56, 0x30, 0x42, 0x42, 0x56, 0x56, 0x30 };
        var image = tape.ToArray();
        var pulses = noisy.Concat(image.Skip(20)).ToArray();
        var rebuilt = new byte[20 + pulses.Length];
        Array.Copy(image, rebuilt, 20);
        BitConverter.GetBytes(pulses.Length).CopyTo(rebuilt, 16);
        pulses.CopyTo(rebuilt, 20);
        Assert.Equal(Prg(0x0801, 9, 9), new TapImage(rebuilt).Read("QUIET").Data);
    }

    [Fact]
    public void ToleratesSlightlyOffPulseLengths()
    {
        var tape = new TapImage();
        tape.Write("WOBBLE", FileType.Prg, Prg(0x0801, Bytes(50)), false);
        var image = tape.ToArray();
        for (int i = 20; i < image.Length; i++)
        {
            if (image[i] == 0x30) image[i] = (byte)(0x30 + (i % 3) - 1);       // +-1 on short pulses
            else if (image[i] == 0x42) image[i] = (byte)(0x42 + (i % 3) - 1);
        }
        Assert.Equal(Prg(0x0801, Bytes(50)), new TapImage(image).Read("WOBBLE").Data);
    }

    [Fact]
    public void Version0ImagesAreRead()
    {
        var tape = new TapImage();
        tape.Write("OLD", FileType.Prg, Prg(0x0801, 5, 6, 7), false);
        var image = tape.ToArray();
        // rewrite as version 0: the version-1 gaps (0 + three bytes) become a single zero
        var pulses = new List<byte>();
        for (int i = 20; i < image.Length; i++)
        {
            if (image[i] == 0) { pulses.Add(0); i += 3; }
            else pulses.Add(image[i]);
        }
        var v0 = new byte[20 + pulses.Count];
        Array.Copy(image, v0, 20);
        v0[12] = 0;
        BitConverter.GetBytes(pulses.Count).CopyTo(v0, 16);
        pulses.CopyTo(v0, 20);
        Assert.Equal(Prg(0x0801, 5, 6, 7), new TapImage(v0).Read("OLD").Data);
    }

    [Fact]
    public void ANewProgramAppendsToAnExistingImage()
    {
        byte[]? saved = null;
        var tape = new TapImage(null, "T", b => saved = b);
        tape.Write("A", FileType.Prg, Prg(0x0801, 1), false);
        var second = new TapImage(saved, save: b => saved = b);
        second.Write("B", FileType.Prg, Prg(0x0801, 2), false);
        var third = new TapImage(saved);
        Assert.Equal(new[] { "A", "B" }, third.Directory().Select(d => d.Name));
    }

    [Fact]
    public void SavingTheSameNameAgainKeepsTheNewestWhenLoading()
    {
        var tape = new TapImage();
        tape.Write("X", FileType.Prg, Prg(0x0801, 1), false);
        Assert.Equal(63, Assert.Throws<DriveException>(() => tape.Write("X", FileType.Prg, Prg(0x0801, 2), false)).Code);
        tape.Write("X", FileType.Prg, Prg(0x0801, 3), true);
        Assert.Equal(Prg(0x0801, 3), Reopen(tape).Read("X").Data);
    }

    [Fact]
    public void ATapeCannotBeEditedInPlace()
    {
        var tape = new TapImage();
        tape.Write("X", FileType.Prg, Prg(0x0801, 1), false);
        Assert.Equal(26, Assert.Throws<DriveException>(() => tape.Scratch("X")).Code);
        Assert.Equal(26, Assert.Throws<DriveException>(() => tape.Rename("X", "Y")).Code);
        Assert.Equal(64, Assert.Throws<DriveException>(() => tape.Write("S", FileType.Seq, new byte[3], false)).Code);
    }

    [Fact]
    public void FormattingErasesTheTape()
    {
        var tape = new TapImage();
        tape.Write("X", FileType.Prg, Prg(0x0801, 1), false);
        tape.Format("BLANK", null);
        Assert.Empty(tape.Directory());
        Assert.Equal(20, tape.ToArray().Length);
    }

    [Fact]
    public void MissingProgramsAreError62()
    {
        Assert.Equal(62, Assert.Throws<DriveException>(() => new TapImage().Read("NOPE")).Code);
        Assert.Equal(62, Assert.Throws<DriveException>(() => new TapImage().Read("")).Code);
    }

    [Fact]
    public void BadImagesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new TapImage(new byte[10]));
        Assert.Throws<InvalidDataException>(() => new TapImage(Encoding.ASCII.GetBytes("C64-TAPE-XXX " + new string(' ', 20))));
    }

    [Fact]
    public void TruncatedImagesDoNotCrash()
    {
        var tape = new TapImage();
        tape.Write("X", FileType.Prg, Prg(0x0801, Bytes(500)), false);
        var image = tape.ToArray();
        var cut = new TapImage(image[..(image.Length / 2)]);
        Assert.Empty(cut.Directory());                                 // the data block is missing
    }

    [Fact]
    public void WildcardNamesMatch()
    {
        var tape = new TapImage();
        tape.Write("GAME ONE", FileType.Prg, Prg(0x0801, 1), false);
        tape.Write("UTIL", FileType.Prg, Prg(0x0801, 2), false);
        var again = Reopen(tape);
        Assert.Equal("GAME ONE", again.Read("GAME*").Name);
        Assert.Equal("UTIL", again.Read("U?IL").Name);
    }

    [Fact]
    public void AMountedTapeWorksWithSaveAndLoad()
    {
        var console = new TestConsole();
        var interp = new C64Basic.Core.Runtime.Interpreter(console, new MemoryFileSystem());
        var tape = new TapImage();
        interp.MountDrive(1, tape);
        interp.ProcessLine("10 PRINT \"FROM TAPE\"");
        interp.ProcessLine("SAVE \"TUNE\",1");
        interp.ProcessLine("NEW");
        interp.ProcessLine("LOAD \"TUNE\",1");
        interp.ProcessLine("RUN");
        Assert.EndsWith("FOUND TUNE\nLOADING\nFROM TAPE\n", console.Output);
        Assert.Single(tape.Directory());
    }
}
