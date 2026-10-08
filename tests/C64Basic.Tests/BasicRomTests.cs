using System.Globalization;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

/// <summary>The BASIC ROM routines (floating point, number printing) called from machine code through USR and SYS.</summary>
public class BasicRomTests
{
    sealed class Machine
    {
        public readonly TestConsole Console = new();
        public readonly Interpreter Interp;
        public Machine() => Interp = new Interpreter(Console, new MemoryFileSystem());
        public byte[] Ram => Interp.Bus.Ram;

        void Load(string hex)
        {
            var code = Convert.FromHexString(hex.Replace(" ", ""));
            Array.Copy(code, 0, Ram, 0xC000, code.Length);
            Ram[785] = 0x4C; Ram[786] = 0x00; Ram[787] = 0xC0;   // USR jumps to $C000
        }

        /// <summary>Runs the routine with the argument in FAC1 and returns FAC1 afterwards.</summary>
        public double Usr(string hex, double argument)
        {
            Load(hex);
            int before = Console.Output.Length;
            Interp.ProcessLine("PRINT USR(" + argument.ToString("R", CultureInfo.InvariantCulture) + ")");
            string text = Console.Output[before..].Trim();
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        public string UsrError(string hex, double argument)
        {
            Load(hex);
            int before = Console.Output.Length;
            Interp.ProcessLine("PRINT USR(" + argument.ToString("R", CultureInfo.InvariantCulture) + ")");
            return Console.Output[before..];
        }

        /// <summary>Keeps a constant in memory at $C300 as a packed float, via MOVMF.</summary>
        public void Constant(double value) => Usr("A2 00 A0 C3 20 D4 BB 60", value);
    }

    static string WithConstant(string call) => "A9 00 A0 C3 " + call + " 60";   // LDA #0: LDY #$C3: JSR ...: RTS

    [Fact]
    public void PackedFloatsUseTheC64Layout()
    {
        var m = new Machine();
        m.Constant(1.5);
        Assert.Equal(new byte[] { 0x81, 0x40, 0, 0, 0 }, m.Ram[0xC300..0xC305]);
        m.Constant(-3.5);
        Assert.Equal(new byte[] { 0x82, 0xE0, 0, 0, 0 }, m.Ram[0xC300..0xC305]);   // negative: the sign replaces the leading 1
        m.Constant(0);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, m.Ram[0xC300..0xC305]);
    }

    [Fact]
    public void MovfmLoadsAndMovmfStoresRoundTrip()
    {
        var m = new Machine();
        m.Constant(-123.456);
        Assert.Equal(-123.456, m.Usr(WithConstant("20 A2 BB"), 0), 6);            // MOVFM overwrites FAC1
    }

    [Theory]
    [InlineData("20 67 B8", 2.25, 1.5, 3.75)]       // FADD:  FAC1 + mem
    [InlineData("20 50 B8", 0.5, 1.5, 1.0)]         // FSUB:  mem - FAC1
    [InlineData("20 28 BA", 2.25, 1.5, 3.375)]      // FMULT: FAC1 * mem
    [InlineData("20 0F BB", 0.5, 1.5, 3.0)]         // FDIV:  mem / FAC1
    [InlineData("20 78 BF", 10, 2, 1024)]           // FPWR:  mem ^ FAC1
    public void ArithmeticWithANumberInMemory(string call, double fac, double memory, double expected)
    {
        var m = new Machine();
        m.Constant(memory);
        Assert.Equal(expected, m.Usr(WithConstant(call), fac), 6);
    }

    [Theory]
    [InlineData("20 6A B8", 2.25, 1.5, 3.75)]       // FADDT: FAC1 + ARG
    [InlineData("20 53 B8", 0.5, 1.5, 1.0)]         // FSUBT: ARG - FAC1
    [InlineData("20 2B BA", 2.25, 1.5, 3.375)]      // FMULTT
    [InlineData("20 12 BB", 0.5, 1.5, 3.0)]         // FDIVT: ARG / FAC1
    [InlineData("20 7B BF", 10, 2, 1024)]           // FPWRT
    public void ArithmeticWithArg(string call, double fac, double arg, double expected)
    {
        var m = new Machine();
        m.Constant(arg);
        // MOVFM the constant, MOVAF it into ARG, then the routine works on the original FAC1 saved on the way
        string code = "A2 00 A0 C4 20 D4 BB " + "A9 00 A0 C3 20 A2 BB 20 0C BC " + "A9 00 A0 C4 20 A2 BB " + call + " 60";
        Assert.Equal(expected, m.Usr(code, fac), 6);
    }

    [Theory]
    [InlineData("20 71 BF", 16, 4)]                 // SQR
    [InlineData("20 CC BC", 3.7, 3)]                // INT
    [InlineData("20 CC BC", -3.2, -4)]              // INT floors
    [InlineData("20 58 BC", -5, 5)]                 // ABS
    [InlineData("20 39 BC", -9, -1)]                // SGN
    [InlineData("20 39 BC", 0, 0)]
    [InlineData("20 B4 BF", 7, -7)]                 // NEGOP
    [InlineData("20 ED BF", 0, 1)]                  // EXP
    [InlineData("20 64 E2", 0, 1)]                  // COS
    [InlineData("20 6B E2", 0, 0)]                  // SIN
    [InlineData("20 0E E3", 1, 0.785398163)]        // ATN
    [InlineData("20 B4 E2", 0, 0)]                  // TAN
    public void FunctionsOfFac1(string call, double argument, double expected)
    {
        var m = new Machine();
        Assert.Equal(expected, m.Usr(call + " 60", argument), 6);
    }

    [Fact]
    public void LogOfEIsOne() => Assert.Equal(1, new Machine().Usr("20 EA B9 60", Math.E), 6);

    [Fact]
    public void RndGivesNumbersBetweenZeroAndOne()
    {
        var m = new Machine();
        for (int i = 0; i < 5; i++)
        {
            double r = m.Usr("20 97 E0 60", 1);
            Assert.InRange(r, 0, 0.999999999);
        }
    }

    [Theory]
    [InlineData(2.0, 1)]    // FAC1 greater than the constant
    [InlineData(1.5, 0)]
    [InlineData(1.0, 255)]
    public void FcompReturnsMinusZeroPlusOne(double fac, int expected)
    {
        var m = new Machine();
        m.Constant(1.5);
        m.Usr(WithConstant("20 5B BC 8D 00 C4").Replace(" 60", "") + " 60", fac);
        Assert.Equal(expected, m.Ram[0xC400]);
    }

    [Fact]
    public void GivayfConvertsASignedInteger()
    {
        var m = new Machine();
        Assert.Equal(256, m.Usr("A9 01 A0 00 20 91 B3 60", 0));
        Assert.Equal(-2, m.Usr("A9 FF A0 FE 20 91 B3 60", 0));
        Assert.Equal(32767, m.Usr("A9 7F A0 FF 20 91 B3 60", 0));
    }

    [Fact]
    public void FacinxConvertsToASignedIntegerInAY()
    {
        var m = new Machine();
        m.Usr("20 AA B1 8D 00 C4 8C 01 C4 60", 1000);                 // STA $C400 (high), STY $C401 (low)
        Assert.Equal(new byte[] { 0x03, 0xE8 }, m.Ram[0xC400..0xC402]);
        m.Usr("20 AA B1 8D 00 C4 8C 01 C4 60", -2);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, m.Ram[0xC400..0xC402]);
    }

    [Fact]
    public void GetadrConvertsToAnUnsignedAddress()
    {
        var m = new Machine();
        m.Usr("20 F7 B7 60", 40000);
        Assert.Equal(new byte[] { 0x40, 0x9C }, m.Ram[0x14..0x16]);
        Assert.Contains("ILLEGAL QUANTITY", m.UsrError("20 F7 B7 60", 70000));
        Assert.Contains("ILLEGAL QUANTITY", m.UsrError("20 F7 B7 60", -1));
    }

    [Fact]
    public void QintGivesA32BitInteger()
    {
        var m = new Machine();
        m.Usr("20 9B BC 60", 100000);
        Assert.Equal(new byte[] { 0, 1, 0x86, 0xA0 }, m.Ram[0x62..0x66]);
    }

    [Fact]
    public void FoutWritesTheNumberAsTextAt0100()
    {
        var m = new Machine();
        m.Usr("20 DD BD 60", 3.5);
        Assert.Equal(" 3.5", System.Text.Encoding.ASCII.GetString(m.Ram[0x100..0x104]));
        Assert.Equal(0, m.Ram[0x104]);
        m.Usr("20 DD BD 60", -0.25);
        Assert.Equal("-.25", System.Text.Encoding.ASCII.GetString(m.Ram[0x100..0x104]));
    }

    [Fact]
    public void FinParsesTheTextAtTheTextPointer()
    {
        var m = new Machine();
        var text = System.Text.Encoding.ASCII.GetBytes("12.5E1");
        Array.Copy(text, 0, m.Ram, 0xC500, text.Length);
        m.Ram[0xC506] = 0;
        m.Ram[0x7A] = 0x00; m.Ram[0x7B] = 0xC5;
        Assert.Equal(125, m.Usr("20 F3 BC 60", 0), 6);
    }

    [Fact]
    public void LinprtPrintsAnUnsignedInteger()
    {
        var m = new Machine();
        m.Interp.ProcessLine("POKE 49152,169:POKE 49153,4:POKE 49154,162:POKE 49155,210:POKE 49156,32:POKE 49157,205:POKE 49158,189:POKE 49159,96");
        m.Interp.ProcessLine("SYS 49152");                    // LDA #4: LDX #$D2: JSR LINPRT: RTS
        Assert.Equal("1234", m.Console.Output);
    }

    [Fact]
    public void StroutPrintsAZeroTerminatedString()
    {
        var m = new Machine();
        var text = new byte[] { 72, 73, 0 };
        Array.Copy(text, 0, m.Ram, 0xC500, 3);
        var code = Convert.FromHexString("A9 00 A0 C5 20 1E AB 60".Replace(" ", ""));
        Array.Copy(code, 0, m.Ram, 0xC000, code.Length);
        m.Interp.ProcessLine("SYS 49152");
        Assert.Equal("HI", m.Console.Output);
    }

    [Fact]
    public void MovfaAndMovafCopyBetweenTheAccumulators()
    {
        var m = new Machine();
        // FAC1 -> ARG, clobber FAC1 by loading 0 from a zeroed constant, ARG -> FAC1
        m.Constant(0);
        Assert.Equal(7.25, m.Usr("20 0C BC " + WithConstant("20 A2 BB").Replace(" 60", "") + " 20 FC BB 60", 7.25), 6);
    }

    // ---------- errors surface as BASIC errors ----------
    [Fact]
    public void SqrOfANegativeNumberIsIllegalQuantity() =>
        Assert.Contains("ILLEGAL QUANTITY", new Machine().UsrError("20 71 BF 60", -1));

    [Fact]
    public void LogOfZeroIsIllegalQuantity() =>
        Assert.Contains("ILLEGAL QUANTITY", new Machine().UsrError("20 EA B9 60", 0));

    [Fact]
    public void DivisionByZeroIsReported()
    {
        var m = new Machine();
        m.Constant(1);
        Assert.Contains("DIVISION BY ZERO", m.UsrError(WithConstant("20 0F BB"), 0));
    }

    [Fact]
    public void OverflowIsReported()
    {
        var m = new Machine();
        m.Constant(1e30);
        Assert.Contains("OVERFLOW", m.UsrError(WithConstant("20 28 BA"), 1e30));
    }
}
