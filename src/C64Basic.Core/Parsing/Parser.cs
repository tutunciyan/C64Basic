using C64Basic.Core.Lexing;
using C64Basic.Core.Runtime;

namespace C64Basic.Core.Parsing;

/// <summary>Recursive-descent parser for one program line.</summary>
public sealed class Parser
{
    static readonly HashSet<string> Funcs = new()
    {
        "SGN", "INT", "ABS", "USR", "FRE", "POS", "SQR", "RND", "LOG", "EXP", "COS", "SIN", "TAN",
        "ATN", "PEEK", "LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$",
    };

        readonly List<Token> _t;
    readonly bool _strict;
    int _p;

    Parser(List<Token> tokens, bool strict) { _t = tokens; _strict = strict; }

    /// <summary>
    /// Parses a line into statements. A syntax error does not throw: statements before the error are
    /// kept and an <see cref="ErrorStmt"/> is appended, so the error surfaces only when execution reaches it.
    /// </summary>
    public static List<Stmt> ParseLine(string text, Lexer lexer)
    {
        var parser = new Parser(lexer.Lex(text), lexer.Strict);
        var stmts = new List<Stmt>();
        try { parser.ParseSeq(stmts); }
        catch (BasicException e) { stmts.Add(new ErrorStmt(e.Code, e.Column)); }
        return stmts;
    }

    // ---- token helpers ----
    Token Cur => _t[_p];

    Token Take()
    {
        var t = _t[_p];
        if (t.Kind != TokKind.Eol) _p++;
        return t;
    }

    static BasicException Syn(Token t) => new(ErrorCode.Syntax, t.Pos);

    bool AtStmtEnd => Cur.Kind is TokKind.Eol or TokKind.Colon || (!_strict && Cur.IsKw("ELSE"));

    Token Expect(TokKind k)
    {
        if (Cur.Kind != k) throw Syn(Cur);
        return Take();
    }

    void ExpectOp(string op)
    {
        if (!Cur.IsOp(op)) throw Syn(Cur);
        Take();
    }

    void ExpectKw(string kw)
    {
        if (!Cur.IsKw(kw)) throw Syn(Cur);
        Take();
    }

    int LineNum()
    {
        var t = Cur;
        if (t.Kind != TokKind.Number || t.Num != Math.Floor(t.Num) || t.Num < 0 || t.Num > 63999) throw Syn(t);
        Take();
        return (int)t.Num;
    }

    static (string Key, VarType Type) MakeKey(string name)
    {
        char last = name[^1];
        var type = last == '$' ? VarType.Str : last == '%' ? VarType.Int : VarType.Real;
        string core = type == VarType.Real ? name : name[..^1];
        if (core.Length > 2) core = core[..2];
        string suffix = type == VarType.Str ? "$" : type == VarType.Int ? "%" : "";
        return (core + suffix, type);
    }

    // ---- statements ----
    void ParseSeq(List<Stmt> o, bool stopAtElse = false)
    {
        while (true)
        {
            if (Cur.Kind == TokKind.Colon) { Take(); continue; }
            if (Cur.Kind == TokKind.Eol) return;
            if (stopAtElse && !_strict && Cur.IsKw("ELSE")) return;
            ParseStatement(o);
            if (Cur.Kind == TokKind.Colon) continue;
            if (Cur.Kind == TokKind.Eol) return;
            if (stopAtElse && !_strict && Cur.IsKw("ELSE")) return;
            throw Syn(Cur);
        }
    }

    void ParseBranch(List<Stmt> o)
    {
        if (Cur.Kind == TokKind.Number) o.Add(new GotoStmt(LineNum()));
        ParseSeq(o, stopAtElse: true);
    }

    void ParseStatement(List<Stmt> o)
    {
        var t = Cur;
        if (t.Kind == TokKind.Name) { o.Add(ParseLet()); return; }
        if (t.Kind != TokKind.Keyword) throw Syn(t);

        switch (t.Text)
        {
            case "LET":
                Take();
                o.Add(ParseLet());
                break;
            case "PRINT":
                Take();
                o.Add(ParsePrint());
                break;
            case "IF":
                ParseIf(o);
                break;
            case "GOTO":
                Take();
                o.Add(new GotoStmt(LineNum()));
                break;
            case "GOSUB":
                Take();
                o.Add(new GosubStmt(LineNum()));
                break;
            case "GO":
                Take();
                ExpectKw("TO");
                o.Add(new GotoStmt(LineNum()));
                break;
            case "RETURN": Take(); o.Add(new ReturnStmt()); break;
            case "END": Take(); o.Add(new EndStmt()); break;
            case "STOP": Take(); o.Add(new StopStmt()); break;
            case "RESTORE": Take(); o.Add(new RestoreStmt()); break;
            case "CLR": Take(); o.Add(new ClrStmt()); break;
            case "NEW": Take(); o.Add(new NewStmt()); break;
            case "CONT": Take(); o.Add(new ContStmt()); break;
            case "ON": o.Add(ParseOn()); break;
            case "FOR": o.Add(ParseFor()); break;
            case "NEXT": o.Add(ParseNext()); break;
            case "DIM": o.Add(ParseDim()); break;
            case "READ": o.Add(ParseRead()); break;
            case "INPUT": o.Add(ParseInput()); break;
            case "GET":
                Take();
                if (Cur.IsOp("#"))
                {
                    Take();
                    var file = ParseExpr();
                    Expect(TokKind.Comma);
                    o.Add(new GetFileStmt(file, ParseLValue()));
                }
                else o.Add(new GetStmt(ParseLValue()));
                break;
            case "OPEN":
                {
                    Take();
                    var args = new List<Expr> { ParseExpr() };
                    while (Cur.Kind == TokKind.Comma && args.Count < 4) { Take(); args.Add(ParseExpr()); }
                    o.Add(new OpenStmt(args.ToArray()));
                    break;
                }
            case "CLOSE":
                Take();
                o.Add(new CloseStmt(ParseExpr()));
                break;
            case "CMD":
                {
                    Take();
                    var file = ParseExpr();
                    Expr? text = null;
                    if (Cur.Kind == TokKind.Comma) { Take(); text = ParseExpr(); }
                    o.Add(new CmdStmt(file, text));
                    break;
                }
            case "WAIT":
                {
                    Take();
                    var addr = ParseExpr();
                    Expect(TokKind.Comma);
                    var mask = ParseExpr();
                    Expr? xor = null;
                    if (Cur.Kind == TokKind.Comma) { Take(); xor = ParseExpr(); }
                    o.Add(new WaitStmt(addr, mask, xor));
                    break;
                }
            case "SYS":
                Take();
                o.Add(new SysStmt(ParseExpr()));
                break;
            case "DATA":
                {
                    Take();
                    string raw = Cur.Kind == TokKind.Raw ? Take().Text : "";
                    o.Add(new DataStmt(raw));
                    break;
                }
            case "REM":
                Take();
                if (Cur.Kind == TokKind.Raw) Take();
                o.Add(new NopStmt());
                break;
            case "POKE":
                {
                    Take();
                    var a = ParseExpr();
                    Expect(TokKind.Comma);
                    o.Add(new PokeStmt(a, ParseExpr()));
                    break;
                }
            case "DEF": o.Add(ParseDef()); break;
            case "RUN":
                Take();
                o.Add(new RunStmt(Cur.Kind == TokKind.Number ? LineNum() : null));
                break;
            case "LIST":
                {
                    Take();
                    var (from, to) = ParseRange();
                    o.Add(new ListStmt(from, to));
                    break;
                }
            case "LOAD": Take(); o.Add(new LoadStmt(ParseFileArgs())); break;
            case "SAVE": Take(); o.Add(new SaveStmt(ParseFileArgs())); break;
            case "VERIFY": Take(); o.Add(new VerifyStmt(ParseFileArgs())); break;

            // ---- extensions ----
            case "RENUMBER":
                {
                    Take();
                    int? start = null, step = null, from = null;
                    if (Cur.Kind == TokKind.Number)
                    {
                        start = LineNum();
                        if (Cur.Kind == TokKind.Comma)
                        {
                            Take();
                            step = LineNum();
                            if (Cur.Kind == TokKind.Comma) { Take(); from = LineNum(); }
                        }
                    }
                    o.Add(new RenumberStmt(start, step, from));
                    break;
                }
            case "AUTO":
                {
                    Take();
                    int? start = null, step = null;
                    if (Cur.Kind == TokKind.Number)
                    {
                        start = LineNum();
                        if (Cur.Kind == TokKind.Comma) { Take(); step = LineNum(); }
                    }
                    o.Add(new AutoStmt(start, step));
                    break;
                }
            case "TRACE":
                {
                    Take();
                    bool? on = null;
                    if (Cur.IsKw("ON")) { Take(); on = true; }
                    else if (Cur.Kind == TokKind.Name && Cur.Text == "OFF") { Take(); on = false; }
                    o.Add(new TraceStmt(on));
                    break;
                }
            case "DELETE":
                {
                    Take();
                    var (from, to) = ParseRange();
                    if (from == null && to == null) throw Syn(Cur);
                    o.Add(new DeleteStmt(from, to));
                    break;
                }
            case "FIND":
                Take();
                o.Add(new FindStmt(Expect(TokKind.String).Text));
                break;

            default:
                throw Syn(t);
        }
    }

    (int? From, int? To) ParseRange()
    {
        int? from = null, to = null;
        if (Cur.Kind == TokKind.Number)
        {
            from = LineNum();
            if (Cur.IsOp("-"))
            {
                Take();
                if (Cur.Kind == TokKind.Number) to = LineNum();
            }
            else to = from;
        }
        else if (Cur.IsOp("-"))
        {
            Take();
            to = LineNum();
        }
        return (from, to);
    }

    FileArgs ParseFileArgs()
    {
        Expr? name = null, device = null, secondary = null;
        if (!AtStmtEnd && Cur.Kind != TokKind.Comma) name = ParseExpr();
        if (Cur.Kind == TokKind.Comma)
        {
            Take();
            if (!AtStmtEnd && Cur.Kind != TokKind.Comma) device = ParseExpr();
            if (Cur.Kind == TokKind.Comma)
            {
                Take();
                if (!AtStmtEnd) secondary = ParseExpr();
            }
        }
        return new FileArgs(name, device, secondary);
    }

    LetStmt ParseLet()
    {
        var target = ParseLValue();
        ExpectOp("=");
        return new LetStmt(target, ParseExpr());
    }

    Expr ParseLValue()
    {
        var name = Expect(TokKind.Name);
        var (key, type) = MakeKey(name.Text);
        if (Cur.Kind == TokKind.LParen) return new ArrayRef(key, type, ParseArgs());
        return new VarRef(key, type);
    }

    Expr[] ParseArgs()
    {
        Expect(TokKind.LParen);
        var args = new List<Expr> { ParseExpr() };
        while (Cur.Kind == TokKind.Comma) { Take(); args.Add(ParseExpr()); }
        Expect(TokKind.RParen);
        return args.ToArray();
    }

    PrintStmt ParsePrint()
    {
        Expr? file = null;
        if (Cur.IsOp("#"))
        {
            Take();
            file = ParseExpr();
            if (Cur.Kind == TokKind.Comma) Take();
        }
        var parts = new List<PrintPart>();
        while (!AtStmtEnd)
        {
            if (Cur.Kind == TokKind.Semi) { Take(); parts.Add(new PrintPart(null, ';')); }
            else if (Cur.Kind == TokKind.Comma) { Take(); parts.Add(new PrintPart(null, ',')); }
            else parts.Add(new PrintPart(ParseExpr(), '\0'));
        }
        return new PrintStmt(parts, file);
    }

    void ParseIf(List<Stmt> o)
    {
        Take(); // IF
        var cond = ParseExpr();
        var ifs = new IfStmt(cond);
        o.Add(ifs);
        if (Cur.IsKw("THEN")) { Take(); ParseBranch(o); }
        else if (Cur.IsKw("GOTO")) ParseSeq(o, stopAtElse: true);
        else throw Syn(Cur);

        if (!_strict && Cur.IsKw("ELSE"))
        {
            Take();
            o.Add(new JumpEndStmt());
            ifs.ElseIdx = o.Count;
            ParseBranch(o);
        }
    }

    Stmt ParseOn()
    {
        Take(); // ON
        var sel = ParseExpr();
        bool gosub;
        if (Cur.IsKw("GOTO")) { Take(); gosub = false; }
        else if (Cur.IsKw("GOSUB")) { Take(); gosub = true; }
        else if (Cur.IsKw("GO")) { Take(); ExpectKw("TO"); gosub = false; }
        else throw Syn(Cur);
        var targets = new List<int> { LineNum() };
        while (Cur.Kind == TokKind.Comma) { Take(); targets.Add(LineNum()); }
        return new OnStmt(sel, gosub, targets.ToArray());
    }

    Stmt ParseFor()
    {
        Take(); // FOR
        var name = Expect(TokKind.Name);
        var (key, type) = MakeKey(name.Text);
        if (type == VarType.Str) throw Syn(name);
        ExpectOp("=");
        var from = ParseExpr();
        ExpectKw("TO");
        var to = ParseExpr();
        Expr? step = null;
        if (Cur.IsKw("STEP")) { Take(); step = ParseExpr(); }
        return new ForStmt(new VarRef(key, type), from, to, step);
    }

    Stmt ParseNext()
    {
        Take(); // NEXT
        var keys = new List<string>();
        while (Cur.Kind == TokKind.Name)
        {
            keys.Add(MakeKey(Take().Text).Key);
            if (Cur.Kind == TokKind.Comma) Take(); else break;
        }
        return new NextStmt(keys.ToArray());
    }

    Stmt ParseDim()
    {
        Take(); // DIM
        var items = new List<DimItem>();
        do
        {
            var name = Expect(TokKind.Name);
            var (key, type) = MakeKey(name.Text);
            items.Add(new DimItem(key, type, ParseArgs()));
        } while (Cur.Kind == TokKind.Comma && Take().Kind == TokKind.Comma);
        return new DimStmt(items.ToArray());
    }

    Stmt ParseRead()
    {
        Take(); // READ
        var targets = new List<Expr> { ParseLValue() };
        while (Cur.Kind == TokKind.Comma) { Take(); targets.Add(ParseLValue()); }
        return new ReadStmt(targets.ToArray());
    }

    Stmt ParseInput()
    {
        Take(); // INPUT
        if (Cur.IsOp("#"))
        {
            Take();
            var file = ParseExpr();
            Expect(TokKind.Comma);
            var fileTargets = new List<Expr> { ParseLValue() };
            while (Cur.Kind == TokKind.Comma) { Take(); fileTargets.Add(ParseLValue()); }
            return new InputFileStmt(file, fileTargets.ToArray());
        }
        string? prompt = null;
        if (Cur.Kind == TokKind.String)
        {
            prompt = Take().Text;
            Expect(TokKind.Semi);
        }
        var targets = new List<Expr> { ParseLValue() };
        while (Cur.Kind == TokKind.Comma) { Take(); targets.Add(ParseLValue()); }
        return new InputStmt(prompt, targets.ToArray());
    }

    Stmt ParseDef()
    {
        Take(); // DEF
        ExpectKw("FN");
        var name = Expect(TokKind.Name);
        string fnKey = "FN" + MakeKey(name.Text).Key;
        Expect(TokKind.LParen);
        var param = Expect(TokKind.Name);
        var (paramKey, paramType) = MakeKey(param.Text);
        if (paramType == VarType.Str) throw Syn(param);
        Expect(TokKind.RParen);
        ExpectOp("=");
        return new DefFnStmt(fnKey, paramKey, ParseExpr());
    }

    // ---- expressions (lowest to highest precedence) ----
    public Expr ParseExpr() => ParseOr();

    Expr ParseOr()
    {
        var l = ParseAnd();
        while (Cur.IsKw("OR")) { Take(); l = new Binary("OR", l, ParseAnd()); }
        return l;
    }

    Expr ParseAnd()
    {
        var l = ParseNot();
        while (Cur.IsKw("AND")) { Take(); l = new Binary("AND", l, ParseNot()); }
        return l;
    }

    Expr ParseNot()
    {
        if (Cur.IsKw("NOT")) { Take(); return new Unary("NOT", ParseNot()); }
        return ParseCompare();
    }

    static bool IsCompare(Token t) => t.Kind == TokKind.Op && t.Text is "=" or "<" or ">" or "<=" or ">=" or "<>";

    Expr ParseCompare()
    {
        var l = ParseAdd();
        while (IsCompare(Cur)) { string op = Take().Text; l = new Binary(op, l, ParseAdd()); }
        return l;
    }

    Expr ParseAdd()
    {
        var l = ParseMul();
        while (Cur.IsOp("+") || Cur.IsOp("-")) { string op = Take().Text; l = new Binary(op, l, ParseMul()); }
        return l;
    }

    Expr ParseMul()
    {
        var l = ParseUnary();
        while (Cur.IsOp("*") || Cur.IsOp("/")) { string op = Take().Text; l = new Binary(op, l, ParseUnary()); }
        return l;
    }

    Expr ParseUnary()
    {
        if (Cur.IsOp("-")) { Take(); return new Unary("-", ParseUnary()); }
        if (Cur.IsOp("+")) { Take(); return ParseUnary(); }
        return ParsePower();
    }

    Expr ParsePower()
    {
        var l = ParsePrimary();
        while (Cur.IsOp("^")) { Take(); l = new Binary("^", l, ParsePowerOperand()); }
        return l;
    }

    Expr ParsePowerOperand()
    {
        if (Cur.IsOp("-")) { Take(); return new Unary("-", ParsePowerOperand()); }
        if (Cur.IsOp("+")) { Take(); return ParsePowerOperand(); }
        return ParsePrimary();
    }

    Expr ParsePrimary()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokKind.Number:
                Take();
                return new NumLit(t.Num);
            case TokKind.String:
                Take();
                return new StrLit(t.Text);
            case TokKind.LParen:
                {
                    Take();
                    var e = ParseExpr();
                    Expect(TokKind.RParen);
                    return e;
                }
            case TokKind.Name:
                {
                    Take();
                    var (key, type) = MakeKey(t.Text);
                    if (Cur.Kind == TokKind.LParen) return new ArrayRef(key, type, ParseArgs());
                    return new VarRef(key, type);
                }
            case TokKind.Keyword:
                if (t.Text is "TAB(" or "SPC(")
                {
                    Take();
                    var arg = ParseExpr();
                    Expect(TokKind.RParen);
                    return new FuncCall(t.Text[..3], new[] { arg });
                }
                if (t.Text == "FN")
                {
                    Take();
                    var name = Expect(TokKind.Name);
                    string key = "FN" + MakeKey(name.Text).Key;
                    Expect(TokKind.LParen);
                    var arg = ParseExpr();
                    Expect(TokKind.RParen);
                    return new UserFnCall(key, arg);
                }
                if (Funcs.Contains(t.Text))
                {
                    Take();
                    return new FuncCall(t.Text, ParseArgs());
                }
                break;
        }
        throw Syn(t);
    }
}
