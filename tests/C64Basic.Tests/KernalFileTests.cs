using C64Basic.Core.Disk;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

/// <summary>The KERNAL's OPEN, CLOSE, CHKIN, CHKOUT, CHRIN, GETIN, CLRCHN and CLALL called from machine code.</summary>
public class KernalFileTests
{
    sealed class Machine
    {
        public readonly TestConsole Console = new();
        public readonly D64Image Disk = D64Image.Create("TEST", "01");
        public readonly Interpreter Interp;
        public byte[] Ram => Interp.Bus.Ram;

        public Machine()
        {
            Interp = new Interpreter(Console, new MemoryFileSystem());
            Interp.MountDrive(8, Disk);
        }

        /// <summary>Puts code at $C000 and a PETSCII name at $C100, runs it with SYS and returns the registers A and P.</summary>
        public (byte A, byte P) Call(string hex, string name = "")
        {
            var code = Convert.FromHexString(hex.Replace(" ", ""));
            Array.Copy(code, 0, Ram, 0xC000, code.Length);
            for (int i = 0; i < name.Length; i++) Ram[0xC100 + i] = (byte)Petscii.ToCode(name[i]);
            Interp.ProcessLine("SYS 49152");
            return (Ram[780], Ram[783]);
        }

        public static bool Carry(byte p) => (p & 1) != 0;

        public static string SetName(int length) => $"A9 {length:X2} A2 00 A0 C1 20 BD FF";                     // SETNAM
        public static string SetLfs(int lf, int dev, int sa) => $"A9 {lf:X2} A2 {dev:X2} A0 {sa:X2} 20 BA FF";   // SETLFS
        public const string Open = "20 C0 FF", Clrchn = "20 CC FF", Clall = "20 E7 FF", Chrin = "20 CF FF", Getin = "20 E4 FF";
        public static string Close(int lf) => $"A9 {lf:X2} 20 C3 FF";
        public static string Chkin(int lf) => $"A2 {lf:X2} 20 C6 FF";
        public static string Chkout(int lf) => $"A2 {lf:X2} 20 C9 FF";
        public static string Chrout(int ch) => $"A9 {ch:X2} 20 D2 FF";
        public const string Rts = "60";
    }

    [Fact]
    public void WritingAFileThroughChkoutAndChrout()
    {
        var m = new Machine();
        m.Call(Machine.SetName(8) + Machine.SetLfs(2, 8, 2) + Machine.Open + Machine.Chkout(2)
               + Machine.Chrout('H') + Machine.Chrout('I') + Machine.Chrout(13)
               + Machine.Clrchn + Machine.Close(2) + Machine.Rts, "DATA,S,W");
        Assert.Equal("HI\r", ((IDiskDrive)m.Disk).ReadText("DATA"));
        Assert.Equal("", m.Console.Output);      // nothing reached the screen
    }

    [Fact]
    public void ReadingAFileThroughChkinAndChrin()
    {
        var m = new Machine();
        ((IDiskDrive)m.Disk).WriteText("DATA", "AB\r", false);
        m.Call(Machine.SetName(4) + Machine.SetLfs(2, 8, 2) + Machine.Open + Machine.Chkin(2)
               + Machine.Chrin + "8D 00 C2" + Machine.Chrin + "8D 01 C2" + "20 B7 FF 8D 02 C2"      // two characters, then READST
               + Machine.Chrin + "8D 03 C2" + "20 B7 FF 8D 04 C2"                                    // the CR, then READST
               + Machine.Clrchn + Machine.Close(2) + Machine.Rts, "DATA");
        Assert.Equal(new byte[] { 65, 66, 0, 13, 64 }, m.Ram[0xC200..0xC205]);   // end of file sets status 64
    }

    [Fact]
    public void GetinReadsFromTheInputChannel()
    {
        var m = new Machine();
        ((IDiskDrive)m.Disk).WriteText("DATA", "Z\r", false);
        m.Call(Machine.SetName(4) + Machine.SetLfs(2, 8, 2) + Machine.Open + Machine.Chkin(2)
               + Machine.Getin + "8D 00 C2" + Machine.Clrchn + Machine.Close(2) + Machine.Rts, "DATA");
        Assert.Equal(90, m.Ram[0xC200]);
    }

    [Fact]
    public void ClrchnReturnsInputAndOutputToTheKeyboardAndScreen()
    {
        var m = new Machine();
        m.Call(Machine.SetName(8) + Machine.SetLfs(2, 8, 2) + Machine.Open + Machine.Chkout(2) + Machine.Clrchn
               + Machine.Chrout('X') + Machine.Close(2) + Machine.Rts, "DATA,S,W");
        Assert.Equal("X", m.Console.Output);     // after CLRCHN CHROUT prints on the screen again
    }

    [Fact]
    public void ClallClosesEveryFileAndFlushesThem()
    {
        var m = new Machine();
        m.Call(Machine.SetName(8) + Machine.SetLfs(2, 8, 2) + Machine.Open + Machine.Chkout(2)
               + Machine.Chrout('Q') + Machine.Chrout(13) + Machine.Clall + Machine.Rts, "DATA,S,W");
        Assert.Equal("Q\r", ((IDiskDrive)m.Disk).ReadText("DATA"));
        m.Interp.ProcessLine("PRINT#2,1");       // the file is closed now
        Assert.Contains("FILE NOT OPEN", m.Console.Output);
    }

    [Fact]
    public void CommandChannelWorksThroughTheKernal()
    {
        var m = new Machine();
        ((IDiskDrive)m.Disk).WriteText("GONE", "X\r", false);
        m.Call(Machine.SetName(0) + Machine.SetLfs(15, 8, 15) + Machine.Open + Machine.Chkout(15)
               + Machine.Chrout('S') + Machine.Chrout(':') + Machine.Chrout('G') + Machine.Chrout('O') + Machine.Chrout('N')
               + Machine.Chrout('E') + Machine.Chrout(13) + Machine.Clrchn + Machine.Close(15) + Machine.Rts);
        Assert.Empty(m.Disk.Directory());
    }

    [Fact]
    public void ErrorsComeBackInAWithTheCarrySet()
    {
        var m = new Machine();
        var (a, p) = m.Call(Machine.Chkin(5) + Machine.Rts);                       // not open
        Assert.True(Machine.Carry(p));
        Assert.Equal(3, a);

        (a, p) = m.Call(Machine.SetName(4) + Machine.SetLfs(3, 8, 2) + Machine.Open + Machine.Rts, "NOPE");
        Assert.True(Machine.Carry(p));
        Assert.Equal(4, a);                                                        // FILE NOT FOUND

        ((IDiskDrive)m.Disk).WriteText("DATA", "A\r", false);
        (a, p) = m.Call(Machine.SetName(4) + Machine.SetLfs(4, 8, 2) + Machine.Open + Machine.Rts, "DATA");
        Assert.False(Machine.Carry(p));
        (a, p) = m.Call(Machine.SetName(4) + Machine.SetLfs(4, 8, 2) + Machine.Open + Machine.Rts, "DATA");
        Assert.True(Machine.Carry(p));
        Assert.Equal(2, a);                                                        // FILE OPEN

        (a, p) = m.Call(Machine.Chkout(4) + Machine.Rts);
        Assert.True(Machine.Carry(p));
        Assert.Equal(7, a);                                                        // NOT OUTPUT FILE

        (a, p) = m.Call(Machine.SetName(7) + Machine.SetLfs(5, 8, 2) + Machine.Open + Machine.Rts, "OUT,S,W");
        (a, p) = m.Call(Machine.Chkin(5) + Machine.Rts);
        Assert.True(Machine.Carry(p));
        Assert.Equal(6, a);                                                        // NOT INPUT FILE

        (a, p) = m.Call(Machine.SetName(4) + Machine.SetLfs(6, 12, 2) + Machine.Open + Machine.Rts, "DATA");
        Assert.True(Machine.Carry(p));
        Assert.Equal(5, a);                                                        // DEVICE NOT PRESENT
    }

    [Fact]
    public void KernalAndBasicShareTheSameChannels()
    {
        var m = new Machine();
        m.Interp.ProcessLine("OPEN 2,8,2,\"DATA,S,W\"");
        m.Call(Machine.Chkout(2) + Machine.Chrout('K') + Machine.Chrout(13) + Machine.Clrchn + Machine.Rts);
        m.Interp.ProcessLine("PRINT#2,\"BASIC\":CLOSE 2");
        Assert.Equal("K\rBASIC\r", ((IDiskDrive)m.Disk).ReadText("DATA"));
    }
}
