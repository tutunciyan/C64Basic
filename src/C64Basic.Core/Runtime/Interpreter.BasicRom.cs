using System.Text;
using C64Basic.Core.Machine;

namespace C64Basic.Core.Runtime;

/// <summary>
/// The floating-point and printing routines of the BASIC ROM that machine code calls (MOVFM, FADD, FMULT, SQR, FOUT, ...),
/// emulated on the 6502 core. Numbers live where the ROM keeps them: FAC1 at $61-$66, ARG at $69-$6E, and in memory as
/// five-byte packed floats.
/// </summary>
public sealed partial class Interpreter
{
    const int Arg = 0x69;

    // ---------- number formats ----------
    /// <summary>A double as the C64's 40-bit float: exponent (0 means zero), 32-bit mantissa with an explicit leading 1, sign.</summary>
    static (byte Exponent, uint Mantissa, bool Negative) Encode(double value)
    {
        if (value == 0 || double.IsNaN(value)) return (0, 0, false);
        double m = Math.Abs(value);
        int exponent = (int)Math.Floor(Math.Log2(m)) + 1;
        double fraction = m / Math.Pow(2, exponent); // in [0.5, 1)
        if (fraction >= 1) { fraction /= 2; exponent++; }
        if (fraction < 0.5) { fraction *= 2; exponent--; }
        long mantissa = (long)Math.Round(fraction * 4294967296.0);
        if (mantissa > 0xFFFFFFFFL) { mantissa = 0x80000000L; exponent++; }

        int biased = exponent + 128;
        if (biased > 255) throw new BasicException(ErrorCode.Overflow);
        if (biased < 1) return (0, 0, false); // underflows to zero
        return ((byte)biased, (uint)mantissa, value < 0);
    }

    static double Decode(int exponent, uint mantissa, bool negative)
    {
        if (exponent == 0) return 0;
        double value = mantissa / 4294967296.0 * Math.Pow(2, exponent - 128);
        return negative ? -value : value;
    }

    /// <summary>Reads an unpacked float (FAC1 or ARG): exponent, four mantissa bytes, sign byte.</summary>
    double ReadFac(int at)
    {
        var r = _bus.Ram;
        uint mantissa = (uint)(r[at + 1] << 24 | r[at + 2] << 16 | r[at + 3] << 8 | r[at + 4]);
        return Decode(r[at], mantissa, (r[at + 5] & 0x80) != 0);
    }

    void WriteFac(int at, double value)
    {
        var r = _bus.Ram;
        var (exponent, mantissa, negative) = Encode(value);
        r[at] = exponent;
        r[at + 1] = (byte)(mantissa >> 24); r[at + 2] = (byte)(mantissa >> 16);
        r[at + 3] = (byte)(mantissa >> 8); r[at + 4] = (byte)mantissa;
        r[at + 5] = exponent != 0 && negative ? (byte)0xFF : (byte)0;
        if (at == Fac1) r[0x70] = 0; // the rounding byte
    }

    /// <summary>Reads a packed float from memory: the mantissa's top bit is the sign.</summary>
    double ReadPacked(int address)
    {
        var r = _bus.Ram;
        int exponent = r[address & 0xFFFF];
        uint mantissa = (uint)(r[(address + 1) & 0xFFFF] << 24 | r[(address + 2) & 0xFFFF] << 16
                             | r[(address + 3) & 0xFFFF] << 8 | r[(address + 4) & 0xFFFF]);
        return Decode(exponent, mantissa | 0x80000000u, (mantissa & 0x80000000u) != 0);
    }

    void WritePacked(int address, double value)
    {
        var r = _bus.Ram;
        var (exponent, mantissa, negative) = Encode(value);
        if (exponent != 0) mantissa = negative ? mantissa | 0x80000000u : mantissa & 0x7FFFFFFFu;
        r[address & 0xFFFF] = exponent;
        r[(address + 1) & 0xFFFF] = (byte)(mantissa >> 24); r[(address + 2) & 0xFFFF] = (byte)(mantissa >> 16);
        r[(address + 3) & 0xFFFF] = (byte)(mantissa >> 8); r[(address + 4) & 0xFFFF] = (byte)mantissa;
    }

    // ---------- the routines ----------
    static TrapResult Done => TrapResult.Return;

    static int AyPointer(Cpu6502 c) => c.A | c.Y << 8;

    double Power(double x, double y)
    {
        if (x == 0 && y < 0) throw new BasicException(ErrorCode.DivisionByZero);
        if (x < 0 && y != Math.Floor(y)) throw new BasicException(ErrorCode.IllegalQuantity);
        return Math.Pow(x, y);
    }

    void RegisterBasicRoutines(Cpu6502 cpu)
    {
        var t = cpu.Traps;

        // moving numbers around
        t[0xBBA2] = c => { WriteFac(Fac1, ReadPacked(AyPointer(c))); return Done; };                       // MOVFM: FAC1 = (A/Y)
        t[0xBBD4] = c => { WritePacked(c.X | c.Y << 8, ReadFac(Fac1)); return Done; };                      // MOVMF: (X/Y) = FAC1
        t[0xBBFC] = c => { WriteFac(Fac1, ReadFac(Arg)); return Done; };                                    // MOVFA: FAC1 = ARG
        t[0xBC0C] = c => { WriteFac(Arg, ReadFac(Fac1)); return Done; };                                    // MOVAF: ARG = FAC1

        // arithmetic with a number in memory (A/Y points to it)
        t[0xB867] = c => Fac(c, f => f + ReadPacked(AyPointer(c)));                                         // FADD:  FAC1 + mem
        t[0xB850] = c => Fac(c, f => ReadPacked(AyPointer(c)) - f);                                         // FSUB:  mem - FAC1
        t[0xBA28] = c => Fac(c, f => f * ReadPacked(AyPointer(c)));                                         // FMULT: FAC1 * mem
        t[0xBB0F] = c => Fac(c, f => Divide(ReadPacked(AyPointer(c)), f));                                  // FDIV:  mem / FAC1
        t[0xBF78] = c => Fac(c, f => Power(ReadPacked(AyPointer(c)), f));                                   // FPWR:  mem ^ FAC1

        // the same with ARG
        t[0xB86A] = c => Fac(c, f => f + ReadFac(Arg));                                                     // FADDT: FAC1 + ARG
        t[0xB853] = c => Fac(c, f => ReadFac(Arg) - f);                                                     // FSUBT: ARG - FAC1
        t[0xBA2B] = c => Fac(c, f => f * ReadFac(Arg));                                                     // FMULTT: FAC1 * ARG
        t[0xBB12] = c => Fac(c, f => Divide(ReadFac(Arg), f));                                              // FDIVT: ARG / FAC1
        t[0xBF7B] = c => Fac(c, f => Power(ReadFac(Arg), f));                                               // FPWRT: ARG ^ FAC1

        // functions of FAC1
        t[0xBC39] = c => Fac(c, f => Math.Sign(f));                                                         // SGN
        t[0xBC58] = c => Fac(c, Math.Abs);                                                                  // ABS
        t[0xBFB4] = c => Fac(c, f => -f);                                                                   // NEGOP
        t[0xBCCC] = c => Fac(c, Math.Floor);                                                                // INT
        t[0xBF71] = c => Fac(c, f => f < 0 ? throw new BasicException(ErrorCode.IllegalQuantity) : Math.Sqrt(f)); // SQR
        t[0xB9EA] = c => Fac(c, f => f <= 0 ? throw new BasicException(ErrorCode.IllegalQuantity) : Math.Log(f));  // LOG
        t[0xBFED] = c => Fac(c, Math.Exp);                                                                  // EXP
        t[0xE264] = c => Fac(c, Math.Cos);                                                                  // COS
        t[0xE26B] = c => Fac(c, Math.Sin);                                                                  // SIN
        t[0xE2B4] = c => Fac(c, Math.Tan);                                                                  // TAN
        t[0xE30E] = c => Fac(c, Math.Atan);                                                                 // ATN
        t[0xE097] = c => Fac(c, f =>                                                                        // RND
        {
            if (f < 0) _rng = new Random(unchecked((int)BitConverter.DoubleToInt64Bits(f)));
            return _rng.NextDouble();
        });

        // comparison: A = 0 equal, 1 if FAC1 is greater than the number in memory, 255 if less
        t[0xBC5B] = c =>
        {
            double f = ReadFac(Fac1), m = ReadPacked(AyPointer(c));
            c.A = f == m ? (byte)0 : f > m ? (byte)1 : (byte)255;
            c.SetNZ(c.A);
            return Done;
        };

        // integers
        t[0xB391] = c => { WriteFac(Fac1, (short)(c.A << 8 | c.Y)); return Done; };                        // GIVAYF: A (high) / Y (low), signed
        t[0xB1AA] = c =>                                                                                    // FACINX: FAC1 to signed A (high) / Y (low)
        {
            int n = Truncate(ReadFac(Fac1), -32768, 32767);
            c.A = (byte)(n >> 8); c.Y = (byte)n;
            return Done;
        };
        t[0xB7F7] = c =>                                                                                    // GETADR: FAC1 to unsigned $14/$15, A (high) / Y (low)
        {
            int n = Truncate(ReadFac(Fac1), 0, 65535);
            _bus.Ram[0x14] = (byte)n; _bus.Ram[0x15] = (byte)(n >> 8);
            c.A = (byte)(n >> 8); c.Y = (byte)n;
            return Done;
        };
        t[0xBC9B] = c =>                                                                                    // QINT: FAC1 to a 32-bit integer in $62-$65
        {
            long n = (long)Math.Truncate(ReadFac(Fac1));
            _bus.Ram[0x62] = (byte)(n >> 24); _bus.Ram[0x63] = (byte)(n >> 16);
            _bus.Ram[0x64] = (byte)(n >> 8); _bus.Ram[0x65] = (byte)n;
            return Done;
        };

        // numbers and strings
        t[0xBDDD] = c =>                                                                                    // FOUT: FAC1 to text at $0100, A/Y = $0100
        {
            string text = NumberFormat.Format(ReadFac(Fac1));
            for (int i = 0; i < text.Length; i++) _bus.Ram[0x100 + i] = (byte)Petscii.ToCode(text[i]);
            _bus.Ram[0x100 + text.Length] = 0;
            c.A = 0; c.Y = 1;
            return Done;
        };
        t[0xBCF3] = c =>                                                                                    // FIN: the text at ($7A) to FAC1
        {
            int at = _bus.Ram[0x7A] | _bus.Ram[0x7B] << 8;
            var sb = new StringBuilder();
            for (int i = 0; i < 48 && _bus.Ram[(at + i) & 0xFFFF] != 0; i++) sb.Append(Petscii.ToChar(_bus.Ram[(at + i) & 0xFFFF]));
            WriteFac(Fac1, Check(NumberParser.ParsePrefix(sb.ToString())));
            return Done;
        };
        t[0xBDCD] = c => { Write(((c.A << 8) | c.X).ToString()); return Done; };                            // LINPRT: print the unsigned integer A (high) / X (low)
        t[0xAB1E] = c =>                                                                                    // STROUT: print the zero-terminated text at A/Y
        {
            int at = AyPointer(c);
            var sb = new StringBuilder();
            for (int i = 0; i < 0x10000 && _bus.Ram[(at + i) & 0xFFFF] != 0; i++) sb.Append(Petscii.ToChar(_bus.Ram[(at + i) & 0xFFFF]));
            Write(sb.ToString());
            return Done;
        };
    }

    TrapResult Fac(Cpu6502 c, Func<double, double> operation)
    {
        WriteFac(Fac1, Check(operation(ReadFac(Fac1))));
        return Done;
    }

    static double Divide(double numerator, double divisor) =>
        divisor == 0 ? throw new BasicException(ErrorCode.DivisionByZero) : numerator / divisor;

    static int Truncate(double value, int min, int max)
    {
        double t = Math.Truncate(value);
        if (t < min || t > max) throw new BasicException(ErrorCode.IllegalQuantity);
        return (int)t;
    }
}
