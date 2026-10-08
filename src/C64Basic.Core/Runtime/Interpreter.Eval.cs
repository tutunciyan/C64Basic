using C64Basic.Core.Parsing;

namespace C64Basic.Core.Runtime;

public sealed partial class Interpreter
{
    // ---------- variables ----------
    static Value DefaultFor(VarType t) => t == VarType.Str ? Value.Empty : Value.Zero;

    double NowSeconds() => (_clockOffsetSeconds + _bus.Seconds()) % 86400;

    Value GetVar(string key, VarType type)
    {
        switch (key)
        {
            case "TI": return Value.Num(Math.Floor(NowSeconds() * 60));
            case "ST": return Value.Num(_st);
            case "TI$":
                {
                    int secs = (int)NowSeconds();
                    return Value.Str($"{secs / 3600:00}{secs / 60 % 60:00}{secs % 60:00}");
                }
        }
        return _vars.TryGetValue(key, out var v) ? v : DefaultFor(type);
    }

    void SetVar(string key, VarType type, Value v)
    {
        v = Coerce(type, v);
        switch (key)
        {
            case "TI":
            case "ST":
                throw new BasicException(ErrorCode.Syntax);
            case "TI$":
                {
                    string s = v.S!;
                    if (s.Length != 6 || !s.All(char.IsAsciiDigit)) throw new BasicException(ErrorCode.IllegalQuantity);
                    int h = int.Parse(s[..2]), m = int.Parse(s[2..4]), sec = int.Parse(s[4..]);
                    if (h > 23 || m > 59 || sec > 59) throw new BasicException(ErrorCode.IllegalQuantity);
                    _clockOffsetSeconds = h * 3600 + m * 60 + sec - _bus.Seconds();
                    return;
                }
        }
        _vars[key] = v;
    }

    static Value Coerce(VarType type, Value v)
    {
        if (type == VarType.Str)
        {
            if (!v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
            return v;
        }
        if (v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
        if (type == VarType.Int)
        {
            double f = Math.Floor(v.N);
            if (f < -32768 || f > 32767) throw new BasicException(ErrorCode.IllegalQuantity);
            return Value.Num(f);
        }
        return v;
    }

    BasicArray ArrayFor(ArrayRef a, out int offset)
    {
        var idx = a.Idx.Select(e => ToInt(Eval(e), 0, int.MaxValue)).ToArray();
        if (!_arrays.TryGetValue(a.Key, out var arr))
        {
            // Undimensioned arrays spring into existence with 11 elements per dimension.
            arr = new BasicArray(Enumerable.Repeat(10, idx.Length).ToArray(), DefaultFor(a.Type));
            _arrays[a.Key] = arr;
        }
        offset = arr.Offset(idx);
        return arr;
    }

    void Assign(Expr target, Value v)
    {
        switch (target)
        {
            case VarRef r:
                SetVar(r.Key, r.Type, v);
                break;
            case ArrayRef a:
                {
                    var arr = ArrayFor(a, out int off);
                    arr.Data[off] = Coerce(a.Type, v);
                    break;
                }
            default:
                throw new BasicException(ErrorCode.Syntax);
        }
    }

    // ---------- numeric helpers ----------
    /// <summary>Applies the MBF range: overflow raises ?OVERFLOW, tiny values flush to zero.</summary>
    double Check(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) throw new BasicException(ErrorCode.Overflow);
        if (!_opts.EmulateMbfRange) return x;
        double a = Math.Abs(x);
        if (a > 1.7014118346e38) throw new BasicException(ErrorCode.Overflow);
        if (a != 0 && a < 1.4693679385e-39) return 0;
        return x;
    }

    /// <summary>Truncates a numeric value to an integer within [min, max] or raises ?ILLEGAL QUANTITY.</summary>
    static int ToInt(Value v, int min, int max)
    {
        if (v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
        double f = Math.Floor(v.N);
        if (f < min || f > max) throw new BasicException(ErrorCode.IllegalQuantity);
        return (int)f;
    }

    double EvalNum(Expr e)
    {
        var v = Eval(e);
        if (v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
        return v.N;
    }

    // ---------- expressions ----------
    Value Eval(Expr e)
    {
        switch (e)
        {
            case NumLit n: return Value.Num(n.V);
            case StrLit s: return Value.Str(s.V);
            case VarRef v: return GetVar(v.Key, v.Type);
            case ArrayRef a:
                {
                    var arr = ArrayFor(a, out int off);
                    return arr.Data[off];
                }
            case Unary u: return EvalUnary(u);
            case Binary b: return EvalBinary(b);
            case FuncCall f: return CallFunc(f);
            case UserFnCall c: return CallUser(c);
            default: throw new BasicException(ErrorCode.Syntax);
        }
    }

    Value EvalUnary(Unary u)
    {
        var v = Eval(u.E);
        if (v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
        if (u.Op == "-") return Value.Num(-v.N);
        return Value.Num(~To16(v.N)); // NOT
    }

    static int To16(double d)
    {
        double f = Math.Floor(d);
        if (f < -32768 || f > 32767) throw new BasicException(ErrorCode.IllegalQuantity);
        return (int)f;
    }

    Value EvalBinary(Binary b)
    {
        var l = Eval(b.L);
        var r = Eval(b.R);

        switch (b.Op)
        {
            case "+":
                if (l.IsStr && r.IsStr)
                {
                    if (l.S!.Length + r.S!.Length > 255) throw new BasicException(ErrorCode.StringTooLong);
                    return Value.Str(l.S + r.S);
                }
                {
                    var (x, y) = Numbers(l, r);
                    return Value.Num(Check(x + y));
                }
            case "-":
                { var (x, y) = Numbers(l, r); return Value.Num(Check(x - y)); }
            case "*":
                { var (x, y) = Numbers(l, r); return Value.Num(Check(x * y)); }
            case "/":
                {
                    var (x, y) = Numbers(l, r);
                    if (y == 0) throw new BasicException(ErrorCode.DivisionByZero);
                    return Value.Num(Check(x / y));
                }
            case "^":
                {
                    var (x, y) = Numbers(l, r);
                    if (x == 0 && y < 0) throw new BasicException(ErrorCode.DivisionByZero);
                    if (x < 0 && y != Math.Floor(y)) throw new BasicException(ErrorCode.IllegalQuantity);
                    return Value.Num(Check(Math.Pow(x, y)));
                }
            case "AND":
                { var (x, y) = Numbers(l, r); return Value.Num(To16(x) & To16(y)); }
            case "OR":
                { var (x, y) = Numbers(l, r); return Value.Num(To16(x) | To16(y)); }
            default:
                {
                    int cmp;
                    if (l.IsStr && r.IsStr) cmp = string.CompareOrdinal(l.S, r.S);
                    else if (!l.IsStr && !r.IsStr) cmp = l.N.CompareTo(r.N);
                    else throw new BasicException(ErrorCode.TypeMismatch);
                    bool result = b.Op switch
                    {
                        "=" => cmp == 0, "<>" => cmp != 0, "<" => cmp < 0,
                        ">" => cmp > 0, "<=" => cmp <= 0, ">=" => cmp >= 0,
                        _ => throw new BasicException(ErrorCode.Syntax),
                    };
                    return Value.Num(result ? -1 : 0);
                }
        }
    }

    static (double A, double B) Numbers(Value l, Value r)
    {
        if (l.IsStr || r.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
        return (l.N, r.N);
    }

    Value CallUser(UserFnCall c)
    {
        if (!_fns.TryGetValue(c.Key, out var fn)) throw new BasicException(ErrorCode.UndefdFunction);
        if (_fnDepth >= 64) throw new BasicException(ErrorCode.FormulaTooComplex);
        double arg = EvalNum(c.Arg);

        bool had = _vars.TryGetValue(fn.Param, out var saved);
        _vars[fn.Param] = Value.Num(arg);
        _fnDepth++;
        try { return Eval(fn.Body); }
        finally
        {
            _fnDepth--;
            if (had) _vars[fn.Param] = saved; else _vars.Remove(fn.Param);
        }
    }

    // ---------- built-in functions ----------
    Value CallFunc(FuncCall f)
    {
        int min = 1, max = 1;
        switch (f.Name)
        {
            case "LEFT$": case "RIGHT$": min = max = 2; break;
            case "MID$": min = 2; max = 3; break;
            case "TAB": case "SPC": throw new BasicException(ErrorCode.Syntax);
        }
        if (f.Args.Length < min || f.Args.Length > max) throw new BasicException(ErrorCode.Syntax);

        double N(int i) => EvalNum(f.Args[i]);
        string S(int i)
        {
            var v = Eval(f.Args[i]);
            if (!v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
            return v.S!;
        }

        switch (f.Name)
        {
            case "ABS": return Value.Num(Math.Abs(N(0)));
            case "SGN": return Value.Num(Math.Sign(N(0)));
            case "INT": return Value.Num(Math.Floor(N(0)));
            case "SQR":
                {
                    double x = N(0);
                    if (x < 0) throw new BasicException(ErrorCode.IllegalQuantity);
                    return Value.Num(Math.Sqrt(x));
                }
            case "LOG":
                {
                    double x = N(0);
                    if (x <= 0) throw new BasicException(ErrorCode.IllegalQuantity);
                    return Value.Num(Math.Log(x));
                }
            case "EXP": return Value.Num(Check(Math.Exp(N(0))));
            case "SIN": return Value.Num(Math.Sin(N(0)));
            case "COS": return Value.Num(Math.Cos(N(0)));
            case "TAN": return Value.Num(Check(Math.Tan(N(0))));
            case "ATN": return Value.Num(Math.Atan(N(0)));
            case "RND":
                {
                    double x = N(0);
                    if (x < 0) _rng = new Random(unchecked((int)BitConverter.DoubleToInt64Bits(x)));
                    return Value.Num(_rng.NextDouble());
                }
            case "PEEK": return Value.Num(Peek(ToInt(Eval(f.Args[0]), 0, 65535)));
            case "FRE": N(0); return Value.Num(BasicBytesFree);
            case "POS": N(0); return Value.Num(Col);
            case "USR": return Value.Num(Check(CallUsr(N(0))));
            case "LEN": return Value.Num(S(0).Length);
            case "VAL": return Value.Num(Check(NumberParser.ParsePrefix(S(0))));
            case "STR$": return Value.Str(NumberFormat.Format(N(0)));
            case "CHR$": return Value.Str(Petscii.ToChar(ToInt(Eval(f.Args[0]), 0, 255)).ToString());
            case "ASC":
                {
                    string s = S(0);
                    if (s.Length == 0) throw new BasicException(ErrorCode.IllegalQuantity);
                    return Value.Num(Petscii.ToCode(s[0]));
                }
            case "LEFT$":
                {
                    string s = S(0);
                    int n = ToInt(Eval(f.Args[1]), 0, 255);
                    return Value.Str(s[..Math.Min(n, s.Length)]);
                }
            case "RIGHT$":
                {
                    string s = S(0);
                    int n = ToInt(Eval(f.Args[1]), 0, 255);
                    return Value.Str(s[Math.Max(0, s.Length - n)..]);
                }
            case "MID$":
                {
                    string s = S(0);
                    int start = ToInt(Eval(f.Args[1]), 1, 255);
                    int len = f.Args.Length == 3 ? ToInt(Eval(f.Args[2]), 0, 255) : s.Length;
                    if (start > s.Length) return Value.Empty;
                    return Value.Str(s.Substring(start - 1, Math.Min(len, s.Length - start + 1)));
                }
        }
        throw new BasicException(ErrorCode.Syntax);
    }
}
