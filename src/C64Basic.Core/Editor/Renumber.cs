using C64Basic.Core.Lexing;
using C64Basic.Core.Runtime;

namespace C64Basic.Core.Editor;

public static class Renumber
{
    /// <summary>
    /// Renumbers every line numbered <paramref name="from"/> or higher to start, start+step, ... and
    /// rewrites the line-number targets of GOTO / GOSUB / THEN / ELSE / RUN / ON..GOTO / ON..GOSUB.
    /// Targets that don't exist are left alone.
    /// </summary>
    public static List<(int Number, string Text)> Apply(
        IReadOnlyList<(int Number, string Text)> lines, int start, int step, int from, Lexer lexer)
    {
        if (step < 1) throw new BasicException(ErrorCode.IllegalQuantity);

        var map = new Dictionary<int, int>();
        int next = start;
        int lastKept = -1;
        foreach (var (num, _) in lines)
        {
            if (num < from) { lastKept = num; continue; }
            if (next > 63999) throw new BasicException(ErrorCode.IllegalQuantity);
            map[num] = next;
            next += step;
        }
        if (map.Count > 0 && lastKept >= start) throw new BasicException(ErrorCode.IllegalQuantity);

        var result = new List<(int, string)>(lines.Count);
        foreach (var (num, text) in lines)
            result.Add((map.TryGetValue(num, out int renum) ? renum : num, Rewrite(text, map, lexer)));
        return result;
    }

    static string Rewrite(string text, Dictionary<int, int> map, Lexer lexer)
    {
        var toks = lexer.Lex(text);
        var edits = new List<(int Pos, int Len, string With)>();
        bool sawOn = false;

        void Retarget(Token t)
        {
            if (t.Kind == TokKind.Number && t.Num == Math.Floor(t.Num) && map.TryGetValue((int)t.Num, out int to))
                edits.Add((t.Pos, t.Len, to.ToString()));
        }

        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind == TokKind.Colon) { sawOn = false; continue; }
            if (t.Kind != TokKind.Keyword) continue;

            switch (t.Text)
            {
                case "ON":
                    sawOn = true;
                    break;
                case "THEN":
                case "ELSE":
                case "RUN":
                    if (i + 1 < toks.Count) Retarget(toks[i + 1]);
                    break;
                case "GOTO":
                case "GOSUB":
                case "TO" when i > 0 && toks[i - 1].IsKw("GO"):
                    {
                        int j = i + 1;
                        if (j < toks.Count) Retarget(toks[j]);
                        if (sawOn)
                        {
                            while (j + 2 < toks.Count && toks[j + 1].Kind == TokKind.Comma)
                            {
                                j += 2;
                                Retarget(toks[j]);
                            }
                        }
                        break;
                    }
            }
        }

        foreach (var (pos, len, with) in edits.OrderByDescending(e => e.Pos))
            text = text.Remove(pos, len).Insert(pos, with);
        return text;
    }
}
