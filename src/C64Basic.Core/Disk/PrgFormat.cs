using System.Text;
using C64Basic.Core.Runtime;

namespace C64Basic.Core.Disk;

/// <summary>
/// The tokenized BASIC V2 program format used by PRG files: a two-byte load address, then for each line a link to the
/// next line, the line number, tokens and a zero byte, and finally a zero link.
/// </summary>
public static class PrgFormat
{
    public const int BasicStart = 0x0801;
    const int FirstToken = 0x80;

    // token $80 + index
    static readonly string[] Tokens =
    {
        "END", "FOR", "NEXT", "DATA", "INPUT#", "INPUT", "DIM", "READ", "LET", "GOTO", "RUN", "IF", "RESTORE",
        "GOSUB", "RETURN", "REM", "STOP", "ON", "WAIT", "LOAD", "SAVE", "VERIFY", "DEF", "POKE", "PRINT#", "PRINT",
        "CONT", "LIST", "CLR", "CMD", "SYS", "OPEN", "CLOSE", "GET", "NEW", "TAB(", "TO", "FN", "SPC(", "THEN", "NOT",
        "STEP", "+", "-", "*", "/", "^", "AND", "OR", ">", "=", "<", "SGN", "INT", "ABS", "USR", "FRE", "POS", "SQR",
        "RND", "LOG", "EXP", "COS", "SIN", "TAN", "ATN", "PEEK", "LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$",
        "RIGHT$", "MID$", "GO",
    };

    const int Rem = 0x8F, Data = 0x83, Print = 0x99, Pi = 0xFF;

    static readonly string[] ByLength = Tokens.OrderByDescending(t => t.Length).ToArray();

    static int TokenAt(string s, int i)
    {
        foreach (var t in ByLength)
        {
            if (i + t.Length <= s.Length && string.Compare(s, i, t, 0, t.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return FirstToken + Array.IndexOf(Tokens, t);
        }
        return -1;
    }

    /// <summary>Crunches one line of source (without its number) into tokens.</summary>
    static void Crunch(string text, List<byte> output)
    {
        bool quoted = false;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (c == '"') { quoted = !quoted; output.Add((byte)'"'); i++; continue; }
            if (quoted) { output.Add(DosText.ToByte(c)); i++; continue; }

            if (c == '?') { output.Add(Print); i++; continue; }
            if (c == 'π') { output.Add(Pi); i++; continue; }
            int token = TokenAt(text, i);
            if (token < 0) { output.Add(DosText.ToByte(c)); i++; continue; }

            output.Add((byte)token);
            i += Tokens[token - FirstToken].Length;
            if (token == Rem)
            {
                for (; i < text.Length; i++) output.Add(DosText.ToByte(text[i]));
            }
            else if (token == Data)
            {
                bool inString = false;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '"') inString = !inString;
                    else if (text[i] == ':' && !inString) break;
                    output.Add(DosText.ToByte(text[i]));
                }
            }
        }
    }

    /// <summary>Builds a PRG from numbered lines (number, source after the number).</summary>
    public static byte[] Tokenize(IEnumerable<(int Number, string Text)> lines, int loadAddress = BasicStart)
    {
        var body = new List<byte>();
        foreach (var (number, text) in lines)
        {
            var line = new List<byte>();
            Crunch(text, line);
            int next = loadAddress + body.Count + 4 + line.Count + 1;
            body.Add((byte)next); body.Add((byte)(next >> 8));
            body.Add((byte)number); body.Add((byte)(number >> 8));
            body.AddRange(line);
            body.Add(0);
        }
        body.Add(0); body.Add(0);

        var prg = new byte[body.Count + 2];
        prg[0] = (byte)loadAddress; prg[1] = (byte)(loadAddress >> 8);
        body.CopyTo(prg, 2);
        return prg;
    }

    /// <summary>Builds a PRG from a text listing (<c>10 PRINT "HI"</c> per line); lines without a number are skipped.</summary>
    public static byte[] FromText(IEnumerable<string> listing)
    {
        var lines = new List<(int, string)>();
        foreach (var raw in listing)
        {
            string line = raw.TrimStart();
            int j = 0;
            while (j < line.Length && char.IsAsciiDigit(line[j])) j++;
            if (j == 0 || !int.TryParse(line.AsSpan(0, j), out int number) || number > 63999) continue;
            lines.Add((number, line[j..].TrimStart(' ').TrimEnd()));
        }
        return Tokenize(lines);
    }

    /// <summary>Expands the tokens of a PRG back into numbered source lines.</summary>
    public static List<(int Number, string Text)> Detokenize(ReadOnlySpan<byte> prg)
    {
        var lines = new List<(int, string)>();
        int pos = 2;
        while (pos + 4 <= prg.Length)
        {
            int link = prg[pos] | prg[pos + 1] << 8;
            if (link == 0) break;
            int number = prg[pos + 2] | prg[pos + 3] << 8;
            pos += 4;

            var sb = new StringBuilder();
            bool quoted = false, raw = false, inData = false;
            for (; pos < prg.Length && prg[pos] != 0; pos++)
            {
                byte b = prg[pos];
                if (b == '"') { quoted = !quoted; sb.Append('"'); continue; }
                if (quoted || raw || (inData && b != ':')) { sb.Append(Petscii.ToChar(b)); continue; }
                inData = false;
                if (b == Pi) sb.Append('π');
                else if (b >= FirstToken && b - FirstToken < Tokens.Length)
                {
                    sb.Append(Tokens[b - FirstToken]);
                    if (b == Rem) raw = true;
                    else if (b == Data) inData = true;
                }
                else sb.Append(Petscii.ToChar(b));
            }
            pos++; // the line's terminating zero
            lines.Add((number, sb.ToString()));
        }
        return lines;
    }

    public static int LoadAddress(ReadOnlySpan<byte> prg) => prg.Length >= 2 ? prg[0] | prg[1] << 8 : 0;
}
