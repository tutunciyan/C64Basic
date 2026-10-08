using C64Basic.Core.Runtime;

namespace C64Basic.Core.Parsing;

public enum VarType { Real, Int, Str }

// ---- Expressions ----
public abstract record Expr;
public sealed record NumLit(double V) : Expr;
public sealed record StrLit(string V) : Expr;
public sealed record VarRef(string Key, VarType Type) : Expr;
public sealed record ArrayRef(string Key, VarType Type, Expr[] Idx) : Expr;
public sealed record Unary(string Op, Expr E) : Expr;
public sealed record Binary(string Op, Expr L, Expr R) : Expr;
public sealed record FuncCall(string Name, Expr[] Args) : Expr;
public sealed record UserFnCall(string Key, Expr Arg) : Expr;

// ---- Statements ----
// A program line is parsed into a flat list of statements. IF/THEN/ELSE is flattened too:
//   IfStmt, <then statements>, [JumpEndStmt, <else statements>]
// so GOSUB/RETURN and FOR/NEXT can resume at any statement index.
public abstract record Stmt;

public sealed record LetStmt(Expr Target, Expr Value) : Stmt;
public sealed record PrintPart(Expr? E, char Sep);
public sealed record PrintStmt(List<PrintPart> Parts, Expr? File = null) : Stmt;
public sealed record IfStmt(Expr Cond) : Stmt { public int ElseIdx { get; set; } = -1; }
public sealed record JumpEndStmt : Stmt;
public sealed record GotoStmt(int Line) : Stmt;
public sealed record GosubStmt(int Line) : Stmt;
public sealed record ReturnStmt : Stmt;
public sealed record OnStmt(Expr Selector, bool Gosub, int[] Targets) : Stmt;
public sealed record ForStmt(VarRef Var, Expr From, Expr To, Expr? Step) : Stmt;
public sealed record NextStmt(string[] Keys) : Stmt;
public sealed record EndStmt : Stmt;
public sealed record StopStmt : Stmt;
public sealed record NopStmt : Stmt;
public sealed record DataStmt(string Raw) : Stmt;
public sealed record ReadStmt(Expr[] Targets) : Stmt;
public sealed record RestoreStmt : Stmt;
public sealed record InputStmt(string? Prompt, Expr[] Targets) : Stmt;
public sealed record GetStmt(Expr Target) : Stmt;
public sealed record InputFileStmt(Expr File, Expr[] Targets) : Stmt;
public sealed record GetFileStmt(Expr File, Expr Target) : Stmt;
public sealed record OpenStmt(Expr[] Args) : Stmt;
public sealed record CloseStmt(Expr File) : Stmt;
public sealed record CmdStmt(Expr File, Expr? Text) : Stmt;
public sealed record WaitStmt(Expr Address, Expr Mask, Expr? Xor) : Stmt;
public sealed record SysStmt(Expr Address) : Stmt;
public sealed record DimItem(string Key, VarType Type, Expr[] Dims);
public sealed record DimStmt(DimItem[] Items) : Stmt;
public sealed record DefFnStmt(string Key, string ParamKey, Expr Body) : Stmt;
public sealed record PokeStmt(Expr Address, Expr Value) : Stmt;
public sealed record ClrStmt : Stmt;
public sealed record NewStmt : Stmt;
public sealed record RunStmt(int? Line) : Stmt;
public sealed record ListStmt(int? From, int? To) : Stmt;
public sealed record LoadStmt(Expr? Name) : Stmt;
public sealed record SaveStmt(Expr? Name) : Stmt;
public sealed record VerifyStmt(Expr? Name) : Stmt;
public sealed record ContStmt : Stmt;

/// <summary>Raised when execution reaches a statement that failed to parse (BASIC reports syntax errors at run time).</summary>
public sealed record ErrorStmt(ErrorCode Code, int Column) : Stmt;

// ---- Extensions ----
public sealed record RenumberStmt(int? Start, int? Step, int? From) : Stmt;
public sealed record AutoStmt(int? Start, int? Step) : Stmt;
public sealed record TraceStmt(bool? On) : Stmt;
public sealed record DeleteStmt(int? From, int? To) : Stmt;
public sealed record FindStmt(string Text) : Stmt;
