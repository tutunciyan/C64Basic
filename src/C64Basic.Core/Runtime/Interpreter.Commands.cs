using C64Basic.Core.Editor;
using C64Basic.Core.Parsing;

namespace C64Basic.Core.Runtime;

public sealed partial class Interpreter
{
    void ExecCommand(Stmt s)
    {
        switch (s)
        {
            case NewStmt:
                _lines.Clear();
                ClearState();
                ProgramChanged();
                _halted = true;
                break;
            case ClrStmt:
                ClearState();
                break;
            case RunStmt r:
                {
                    ClearState();
                    _canCont = false;
                    if (r.Line is int line) JumpTo(line);
                    else { _curLine = 0; _curStmt = 0; }
                    break;
                }
            case ContStmt:
                if (!_canCont) throw new BasicException(ErrorCode.CantContinue);
                _canCont = false;
                _curLine = _contLine;
                _curStmt = _contStmt;
                break;
            case ListStmt l:
                ListLines(l.From, l.To);
                break;
            case LoadStmt l:
                DoLoad(l.Args, verify: false);
                break;
            case SaveStmt sv:
                DoSave(sv.Args);
                break;
            case VerifyStmt v:
                DoLoad(v.Args, verify: true);
                break;

            // ---- extensions ----
            case RenumberStmt r:
                DoRenumber(r);
                break;
            case AutoStmt a:
                AutoNext = a.Start ?? 10;
                AutoStep = a.Step ?? 10;
                if (AutoStep < 1) throw new BasicException(ErrorCode.IllegalQuantity);
                AutoActive = true;
                _halted = true;
                break;
            case TraceStmt t:
                _trace = t.On ?? !_trace;
                break;
            case DeleteStmt d:
                {
                    int from = d.From ?? 0, to = d.To ?? MaxLineNumber;
                    if (_lines.RemoveAll(l => l.Number >= from && l.Number <= to) > 0) ProgramChanged();
                    break;
                }
            case FindStmt f:
                foreach (var l in _lines.ToList())
                {
                    if (l.Text.Contains(f.Text, StringComparison.OrdinalIgnoreCase))
                        Write(LineListing(l) + "\n");
                }
                break;

            default:
                throw new BasicException(ErrorCode.Syntax);
        }
    }

    void ListLines(int? from, int? to)
    {
        foreach (var l in _lines.ToList())
        {
            if (from != null && l.Number < from) continue;
            if (to != null && l.Number > to) break;
            if (_dev.BreakRequested)
            {
                _dev.BreakRequested = false;
                NewLineIfNeeded();
                Write("BREAK\n");
                return;
            }
            Write(LineListing(l) + "\n");
        }
    }

    void DoRenumber(RenumberStmt r)
    {
        int start = r.Start ?? 10, step = r.Step ?? 10, from = r.From ?? 0;
        var renumbered = Renumber.Apply(_lines.Select(l => (l.Number, l.Text)).ToList(), start, step, from, _lexer);
        _lines.Clear();
        foreach (var (number, text) in renumbered)
            _lines.Add(new ProgramLine { Number = number, Text = text });
        ProgramChanged();
        _halted = true; // line numbers moved, so a running program can't continue
    }

    /// <summary>Called by the REPL while AUTO is active with the text the user typed after the prefilled number.</summary>
    public void ProcessAutoLine(string text)
    {
        if (text.Trim().Length == 0)
        {
            AutoActive = false;
            return;
        }
        int number = AutoNext;
        AutoNext += AutoStep;
        if (number > MaxLineNumber) { AutoActive = false; return; }
        ProcessLine($"{number} {text}");
    }
}
