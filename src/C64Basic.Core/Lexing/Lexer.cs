using System.Globalization;
using System.Text;

namespace C64Basic.Core.Lexing;

public enum TokKind { Number, String, Name, Keyword, Op, Colon, Comma, Semi, LParen, RParen, Raw, Eol }

/// <summary>A lexical token. Pos/Len describe the span in the original line text.</summary>
public readonly record struct Token(TokKind Kind, string Text, int Pos, int Len, double Num = 0)
{
    public bool IsKw(string kw) => Kind == TokKind.Keyword && Text == kw;
    public bool IsOp(string op) => Kind == TokKind.Op && Text == op;
}

public static class Keywords
{
    public static readonly string[] Standard =
    {
        "END", "FOR", "NEXT", "DATA", "INPUT", "DIM", "READ", "LET", "GOTO", "RUN", "IF", "RESTORE",
        "GOSUB", "RETURN", "REM", "STOP", "ON", "WAIT", "LOAD", "SAVE", "VERIFY", "DEF", "POKE",
        "PRINT", "CONT", "LIST", "CLR", "CMD", "SYS", "OPEN", "CLOSE", "GET", "NEW", "TAB(", "TO",
        "FN", "SPC(", "THEN", "NOT", "STEP", "AND", "OR", "SGN", "INT", "ABS", "USR", "FRE", "POS",
        "SQR", "RND", "LOG", "EXP", "COS", "SIN", "TAN", "ATN", "PEEK", "LEN", "STR$", "VAL", "ASC",
        "CHR$", "LEFT$", "RIGHT$", "MID$", "GO",
    };

    /// <summary>Keywords that only exist in the extended (non-strict) dialect.</summary>
    public static readonly string[] Extensions = { "ELSE", "RENUMBER", "AUTO", "TRACE", "DELETE", "FIND" };
}

/// <summary>
/// Splits one program line into tokens the way BASIC V2 does: keywords are recognised anywhere
/// (so FORI=1TO10 works, and a variable like TOTAL is read as TO + TAL), spaces are ignored
/// outside strings, and REM / DATA swallow the rest of the statement as raw text.
/// </summary>
public sealed class Lexer
{
    readonly string[] _keywords;

    public bool Strict { get; }

    public Lexer(bool strict)
    {
        Strict = strict;
        var all = strict ? Keywords.Standard : Keywords.Standard.Concat(Keywords.Extensions);
        _keywords = all.OrderByDescending(k => k.Length).ToArray();
    }

    string? MatchKeyword(string s, int i)
    {
        foreach (var k in _keywords)
        {
            if (i + k.Length <= s.Length && string.Compare(s, i, k, 0, k.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return k;
        }
        return null;
    }

    public List<Token> Lex(string s)
    {
        var toks = new List<Token>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == ' ') { i++; continue; }

            if (c == '"')
            {
                int end = s.IndexOf('"', i + 1);
                int close = end < 0 ? n : end;
                toks.Add(new Token(TokKind.String, s.Substring(i + 1, close - i - 1), i, (end < 0 ? n : end + 1) - i));
                i = end < 0 ? n : end + 1;
                continue;
            }

            if (char.IsAsciiDigit(c) || c == '.')
            {
                int start = i;
                while (i < n && char.IsAsciiDigit(s[i])) i++;
                if (i < n && s[i] == '.') { i++; while (i < n && char.IsAsciiDigit(s[i])) i++; }
                if (i < n && (s[i] == 'E' || s[i] == 'e'))
                {
                    int j = i + 1;
                    if (j < n && (s[j] == '+' || s[j] == '-')) j++;
                    if (j < n && char.IsAsciiDigit(s[j]))
                    {
                        while (j < n && char.IsAsciiDigit(s[j])) j++;
                        i = j;
                    }
                }
                string text = s.Substring(start, i - start);
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
                toks.Add(new Token(TokKind.Number, text, start, i - start, v));
                continue;
            }

            if (c == '?')
            {
                toks.Add(new Token(TokKind.Keyword, "PRINT", i, 1));
                i++;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                string? kw = MatchKeyword(s, i);
                if (kw != null)
                {
                    toks.Add(new Token(TokKind.Keyword, kw, i, kw.Length));
                    i += kw.Length;
                    if (kw == "REM")
                    {
                        if (i < n) toks.Add(new Token(TokKind.Raw, s.Substring(i), i, n - i));
                        i = n;
                    }
                    else if (kw == "DATA")
                    {
                        int start = i;
                        bool inQuote = false;
                        while (i < n && (inQuote || s[i] != ':'))
                        {
                            if (s[i] == '"') inQuote = !inQuote;
                            i++;
                        }
                        if (i > start) toks.Add(new Token(TokKind.Raw, s.Substring(start, i - start), start, i - start));
                    }
                    continue;
                }

                int nameStart = i;
                var sb = new StringBuilder();
                while (i < n && char.IsAsciiLetterOrDigit(s[i]))
                {
                    if (i > nameStart && char.IsAsciiLetter(s[i]) && MatchKeyword(s, i) != null) break;
                    sb.Append(char.ToUpperInvariant(s[i]));
                    i++;
                }
                if (i < n && (s[i] == '$' || s[i] == '%')) { sb.Append(s[i]); i++; }
                toks.Add(new Token(TokKind.Name, sb.ToString(), nameStart, i - nameStart));
                continue;
            }

            switch (c)
            {
                case ':': toks.Add(new Token(TokKind.Colon, ":", i, 1)); i++; break;
                case ',': toks.Add(new Token(TokKind.Comma, ",", i, 1)); i++; break;
                case ';': toks.Add(new Token(TokKind.Semi, ";", i, 1)); i++; break;
                case '(': toks.Add(new Token(TokKind.LParen, "(", i, 1)); i++; break;
                case ')': toks.Add(new Token(TokKind.RParen, ")", i, 1)); i++; break;
                case '<':
                case '>':
                case '=':
                    {
                        string two = i + 1 < n ? s.Substring(i, 2) : "";
                        string? norm = two switch
                        {
                            "<=" => "<=", ">=" => ">=", "<>" => "<>", "=<" => "<=", "=>" => ">=", "><" => "<>", _ => null,
                        };
                        if (norm != null) { toks.Add(new Token(TokKind.Op, norm, i, 2)); i += 2; }
                        else { toks.Add(new Token(TokKind.Op, c.ToString(), i, 1)); i++; }
                        break;
                    }
                default:
                    toks.Add(new Token(TokKind.Op, c.ToString(), i, 1));
                    i++;
                    break;
            }
        }
        toks.Add(new Token(TokKind.Eol, "", n, 0));
        return toks;
    }

    /// <summary>
    /// Returns the line with keywords and names upper-cased and '?' expanded to PRINT, as LIST would
    /// show it. Spacing, strings, numbers, REM and DATA text are kept exactly as typed.
    /// </summary>
    public string Normalize(string text)
    {
        var sb = new StringBuilder();
        int pos = 0;
        foreach (var t in Lex(text))
        {
            if (t.Kind == TokKind.Eol) break;
            sb.Append(text, pos, t.Pos - pos);
            if (t.Kind is TokKind.Keyword or TokKind.Name)
            {
                sb.Append(t.Text);
                // '?A' lists as PRINTA on a real C64; the extended dialect keeps it readable.
                bool shorthand = t.Kind == TokKind.Keyword && t.Len == 1;
                if (!Strict && shorthand && t.Pos + 1 < text.Length && text[t.Pos + 1] is not (' ' or ':'))
                    sb.Append(' ');
            }
            else sb.Append(text, t.Pos, t.Len);
            pos = t.Pos + t.Len;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
