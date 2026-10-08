using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class ExpressionTests
{
    [Theory]
    [InlineData("PRINT 1+2*3", " 7 \n")]
    [InlineData("PRINT (1+2)*3", " 9 \n")]
    [InlineData("PRINT -2^2", "-4 \n")]
    [InlineData("PRINT 2^-1", " .5 \n")]
    [InlineData("PRINT 2^3^2", " 64 \n")]
    [InlineData("PRINT 10/4", " 2.5 \n")]
    [InlineData("PRINT 1/3", " .333333333 \n")]
    [InlineData("PRINT 1E9", " 1E+09 \n")]
    [InlineData("PRINT 123456789", " 123456789 \n")]
    [InlineData("PRINT 0.01", " .01 \n")]
    [InlineData("PRINT 0.001", " 1E-03 \n")]
    [InlineData("PRINT -5", "-5 \n")]
    [InlineData("PRINT 1=1", "-1 \n")]
    [InlineData("PRINT 1=2", " 0 \n")]
    [InlineData("PRINT 5 AND 3", " 1 \n")]
    [InlineData("PRINT 5 OR 3", " 7 \n")]
    [InlineData("PRINT NOT 0", "-1 \n")]
    [InlineData("PRINT NOT 1=2", "-1 \n")]
    [InlineData("PRINT \"A\"+\"B\"", "AB\n")]
    [InlineData("PRINT \"A\"<\"B\"", "-1 \n")]
    [InlineData("?3", " 3 \n")]
    public void Evaluates(string line, string expected)
    {
        Assert.Equal(expected + "READY.\n".Replace("READY.\n", ""), Basic.Run(line));
    }

    [Fact]
    public void KeywordsAreFoundInsideNames()
    {
        // Real BASIC V2 quirk: FORI=1TO3 works without spaces.
        var output = Basic.Run("10 FORI=1TO3:PRINTI;:NEXT", "RUN");
        Assert.Equal(" 1  2  3 ", output);
    }

    [Fact]
    public void ReportsDivisionByZero()
    {
        Assert.Equal("?DIVISION BY ZERO  ERROR\n", Basic.Run("PRINT 1/0"));
    }

    [Fact]
    public void ReportsOverflow()
    {
        Assert.Equal("?OVERFLOW  ERROR\n", Basic.Run("PRINT 1E38*10"));
    }

    [Fact]
    public void ReportsTypeMismatch()
    {
        Assert.Equal("?TYPE MISMATCH  ERROR\n", Basic.Run("PRINT \"A\"+1"));
    }

    [Fact]
    public void IntegerVariablesTruncateTowardMinusInfinity()
    {
        Assert.Equal(" 1 \n-2 \n", Basic.Run("A%=1.9:PRINT A%", "A%=-1.5:PRINT A%"));
    }

    [Fact]
    public void IntegerVariableOutOfRangeIsIllegalQuantity()
    {
        Assert.Equal("?ILLEGAL QUANTITY  ERROR\n", Basic.Run("A%=40000"));
    }
}

public class FunctionTests
{
    [Theory]
    [InlineData("PRINT ABS(-3)", " 3 \n")]
    [InlineData("PRINT INT(-1.5)", "-2 \n")]
    [InlineData("PRINT SGN(-9)", "-1 \n")]
    [InlineData("PRINT SQR(16)", " 4 \n")]
    [InlineData("PRINT LEN(\"HELLO\")", " 5 \n")]
    [InlineData("PRINT LEFT$(\"HELLO\",2)", "HE\n")]
    [InlineData("PRINT RIGHT$(\"HELLO\",2)", "LO\n")]
    [InlineData("PRINT MID$(\"HELLO\",2,3)", "ELL\n")]
    [InlineData("PRINT MID$(\"HELLO\",4)", "LO\n")]
    [InlineData("PRINT ASC(\"A\")", " 65 \n")]
    [InlineData("PRINT CHR$(66)", "B\n")]
    [InlineData("PRINT VAL(\"12ABC\")", " 12 \n")]
    [InlineData("PRINT VAL(\"ABC\")", " 0 \n")]
    [InlineData("PRINT STR$(5)", " 5\n")]
    [InlineData("PRINT STR$(-5)", "-5\n")]
    [InlineData("PRINT PEEK(1000)", " 0 \n")]
    public void Evaluates(string line, string expected)
    {
        Assert.Equal(expected, Basic.Run(line));
    }

    [Fact]
    public void PokeThenPeek()
    {
        Assert.Equal(" 42 \n", Basic.Run("POKE 1024,42:PRINT PEEK(1024)"));
    }

    [Fact]
    public void NegativeRndSeedIsRepeatable()
    {
        var output = Basic.Run("X=RND(-5):A=RND(1):X=RND(-5):B=RND(1):PRINT A=B");
        Assert.Equal("-1 \n", output);
    }

    [Fact]
    public void UserDefinedFunction()
    {
        Assert.Equal(" 25 \n", Basic.Run("10 DEF FNSQ(X)=X*X", "20 PRINT FNSQ(5)", "RUN"));
    }

    [Fact]
    public void UndefinedFunction()
    {
        Assert.Equal("?UNDEF'D FUNCTION  ERROR IN 10\n", Basic.RunWith(null, new InterpreterOptions { Strict = true }, "10 PRINT FNA(1)", "RUN"));
    }
}

public class PrintTests
{
    [Fact]
    public void SemicolonJoinsAndSuppressesNewline()
    {
        Assert.Equal("AB", Basic.Run("PRINT \"A\";\"B\";"));
    }

    [Fact]
    public void CommaTabsToTenColumnZones()
    {
        Assert.Equal("A         B\n", Basic.Run("PRINT \"A\",\"B\""));
    }

    [Fact]
    public void TabAndSpc()
    {
        Assert.Equal("   X  Y\n", Basic.Run("PRINT TAB(3);\"X\";SPC(2);\"Y\""));
    }

    [Fact]
    public void ErrorStartsOnFreshLineWhenCursorIsNotAtColumnZero()
    {
        Assert.Equal("X\n?SYNTAX  ERROR\n", Basic.Run("PRINT \"X\";:PRINT )"));
    }
}

public class ProgramFlowTests
{
    [Fact]
    public void ForNextWithStep()
    {
        var output = Basic.Run("10 FOR I=10 TO 1 STEP -3:PRINT I;:NEXT I", "RUN");
        Assert.Equal(" 10  7  4  1 ", output);
    }

    [Fact]
    public void ForLoopRunsOnceWhenStartPastLimit()
    {
        // BASIC V2 tests the limit at NEXT, so the body always runs once.
        Assert.Equal(" 5 ", Basic.Run("10 FOR I=5 TO 1:PRINT I;:NEXT", "RUN"));
    }

    [Fact]
    public void NestedLoopsWithCombinedNext()
    {
        var output = Basic.Run("10 FOR I=1 TO 2:FOR J=1 TO 2:PRINT I*10+J;:NEXT J,I", "RUN");
        Assert.Equal(" 11  12  21  22 ", output);
    }

    [Fact]
    public void GosubReturn()
    {
        var output = Basic.Run("10 GOSUB 100:PRINT \"B\":END", "100 PRINT \"A\":RETURN", "RUN");
        Assert.Equal("A\nB\n", output);
    }

    [Fact]
    public void ReturnWithoutGosub()
    {
        Assert.Equal("?RETURN WITHOUT GOSUB  ERROR IN 10\n", Basic.Run("10 RETURN", "RUN"));
    }

    [Fact]
    public void NextWithoutFor()
    {
        Assert.Equal("?NEXT WITHOUT FOR  ERROR IN 10\n", Basic.Run("10 NEXT", "RUN"));
    }

    [Fact]
    public void GotoUndefinedLine()
    {
        Assert.Equal("?UNDEF'D STATEMENT  ERROR IN 10\n", Basic.Run("10 GOTO 99", "RUN"));
    }

    [Fact]
    public void OnGotoAndOnGosub()
    {
        var output = Basic.Run(
            "10 FOR K=0 TO 3",
            "20 ON K GOTO 100,200",
            "30 PRINT \"N\";:GOTO 300",
            "100 PRINT \"A\";:GOTO 300",
            "200 PRINT \"B\";",
            "300 NEXT",
            "RUN");
        // K=0 and K=3 are out of range and fall through; K=1 and K=2 jump.
        Assert.Equal("NABN", output);
    }

    [Fact]
    public void OnGosubReturnsToNextStatement()
    {
        var output = Basic.Run(
            "10 ON 2 GOSUB 100,200:PRINT \"done\":END",
            "100 PRINT \"one\":RETURN",
            "200 PRINT \"two\":RETURN",
            "RUN");
        Assert.Equal("two\ndone\n", output);
    }

    [Fact]
    public void IfThenSkipsRestOfLineWhenFalse()
    {
        var output = Basic.Run("10 IF 1=2 THEN PRINT \"A\":PRINT \"B\"", "20 PRINT \"C\"", "RUN");
        Assert.Equal("C\n", output);
    }

    [Fact]
    public void IfThenLineNumber()
    {
        var output = Basic.Run("10 IF 1=1 THEN 30", "20 PRINT \"no\"", "30 PRINT \"yes\"", "RUN");
        Assert.Equal("yes\n", output);
    }

    [Fact]
    public void IfElseExtension()
    {
        var output = Basic.Run("10 FOR I=1 TO 2:IF I=1 THEN PRINT \"one\" ELSE PRINT \"other\"", "20 NEXT", "RUN");
        Assert.Equal("one\nother\n", output);
    }

    [Fact]
    public void GosubInsideThenContinuesRestOfThenClause()
    {
        var output = Basic.Run("10 IF 1=1 THEN GOSUB 100:PRINT \"after\"", "20 END", "100 PRINT \"sub\":RETURN", "RUN");
        Assert.Equal("sub\nafter\n", output);
    }

    [Fact]
    public void EndStopsAndContResumes()
    {
        var output = Basic.Run("10 PRINT \"A\":END", "20 PRINT \"B\"", "RUN", "CONT");
        Assert.Equal("A\nB\n", output);
    }

    [Fact]
    public void StopPrintsBreakAndContResumes()
    {
        var output = Basic.Run("10 PRINT \"A\":STOP:PRINT \"B\"", "RUN", "CONT");
        Assert.Equal("A\nBREAK IN 10\nB\n", output);
    }

    [Fact]
    public void ContAfterEditCantContinue()
    {
        var output = Basic.Run("10 STOP", "RUN", "20 REM", "CONT");
        Assert.Equal("BREAK IN 10\n?CAN'T CONTINUE  ERROR\n", output);
    }

    [Fact]
    public void BreakKeyStopsRunningProgram()
    {
        var console = new TestConsole { BreakAfterWrites = 3 };
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine("10 PRINT \"X\";:GOTO 10");
        interp.ProcessLine("RUN");
        Assert.Equal("XXX\nBREAK IN 10\n", console.Output);
    }
}

public class DataTests
{
    [Fact]
    public void ReadAndRestore()
    {
        var output = Basic.Run(
            "10 DATA 1,2,\"A,B\",hello",
            "20 READ A,B,C$,D$:PRINT A;B;C$;D$",
            "30 RESTORE:READ X:PRINT X",
            "RUN");
        Assert.Equal(" 1  2 A,Bhello\n 1 \n", output);
    }

    [Fact]
    public void OutOfData()
    {
        Assert.Equal("?OUT OF DATA  ERROR IN 10\n", Basic.Run("10 READ A", "RUN"));
    }

    [Fact]
    public void ReadingTextIntoNumberIsSyntaxError()
    {
        Assert.StartsWith("?SYNTAX  ERROR IN 20", Basic.Run("10 DATA HELLO", "20 READ A", "RUN"));
    }
}

public class ArrayTests
{
    [Fact]
    public void DimAndUse()
    {
        var output = Basic.Run("10 DIM A(3),B$(1,1)", "20 A(2)=7:B$(1,0)=\"X\":PRINT A(2);B$(1,0)", "RUN");
        Assert.Equal(" 7 X\n", output);
    }

    [Fact]
    public void ImplicitArraysHaveElevenElements()
    {
        Assert.Equal(" 5 \n", Basic.Run("10 A(10)=5:PRINT A(10)", "RUN"));
        Assert.Equal("?BAD SUBSCRIPT  ERROR IN 10\n", Basic.Run("10 A(11)=5", "RUN"));
    }

    [Fact]
    public void DimTwiceIsRedimd()
    {
        Assert.Equal("?REDIM'D ARRAY  ERROR IN 20\n", Basic.Run("10 DIM A(2)", "20 DIM A(3)", "RUN"));
    }

    [Fact]
    public void NegativeSubscriptIsIllegalQuantity()
    {
        Assert.Equal("?ILLEGAL QUANTITY  ERROR IN 10\n", Basic.Run("10 A(-1)=1", "RUN"));
    }
}

public class InputTests
{
    [Fact]
    public void InputReadsValues()
    {
        var output = Basic.RunWith(new[] { "42", "BOB" }, null,
            "10 INPUT \"NUM\";N", "20 INPUT S$", "30 PRINT N;S$", "RUN");
        // "NUM? " and "? " are the two prompts; PRINT then adds a leading space to the positive number.
        Assert.Equal("NUM? ?  42 BOB\n", output);
    }

    [Fact]
    public void InputRepromptsOnBadNumber()
    {
        var output = Basic.RunWith(new[] { "abc", "7" }, null, "10 INPUT N:PRINT N", "RUN");
        Assert.Equal("? ?REDO FROM START\n?  7 \n", output);
    }

    [Fact]
    public void InputIsIllegalInDirectMode()
    {
        Assert.Equal("?ILLEGAL DIRECT  ERROR\n", Basic.Run("INPUT A"));
    }
}

public class ErrorReportingTests
{
    [Fact]
    public void SyntaxErrorAppearsOnlyWhenExecutionReachesIt()
    {
        var output = Basic.RunStrict("10 PRINT \"OK\"", "20 PRINT )", "RUN");
        Assert.Equal("OK\n?SYNTAX  ERROR IN 20\n", output);
    }

    [Fact]
    public void ExtendedModeShowsSourceExcerptAndCaret()
    {
        var output = Basic.Run("10 PRINT 1 +", "RUN");
        Assert.Equal("?SYNTAX  ERROR IN 10\n10 PRINT 1 +\n            ^\n", output);
    }

    [Fact]
    public void StrictModeOmitsExcerpt()
    {
        Assert.Equal("?SYNTAX  ERROR IN 10\n", Basic.RunStrict("10 PRINT 1 +", "RUN"));
    }

    [Fact]
    public void StrictModeHasNoElseKeyword()
    {
        Assert.Equal("?SYNTAX  ERROR IN 10\n", Basic.RunStrict("10 IF 1=1 THEN PRINT 1 ELSE PRINT 2", "RUN"));
    }

    [Fact]
    public void LinesAreLimitedTo80CharactersInStrictMode()
    {
        var longLine = "10 REM " + new string('X', 100);
        Assert.Equal("?STRING TOO LONG  ERROR\n", Basic.RunStrict(longLine));
        Assert.Equal("", Basic.Run(longLine));
    }
}

public class EditorTests
{
    [Fact]
    public void ListNormalisesKeywordsAndExpandsQuestionMark()
    {
        var output = Basic.Run("10 print \"hi\"", "20 ?a", "LIST");
        Assert.Equal("10 PRINT \"hi\"\n20 PRINT A\n", output);
    }

    [Fact]
    public void ListRanges()
    {
        var setup = new[] { "10 REM A", "20 REM B", "30 REM C", "40 REM D" };
        Assert.Equal("20 REM B\n30 REM C\n", Basic.Run(setup.Append("LIST 20-30").ToArray()));
        Assert.Equal("10 REM A\n20 REM B\n", Basic.Run(setup.Append("LIST -20").ToArray()));
        Assert.Equal("30 REM C\n40 REM D\n", Basic.Run(setup.Append("LIST 30-").ToArray()));
        Assert.Equal("30 REM C\n", Basic.Run(setup.Append("LIST 30").ToArray()));
    }

    [Fact]
    public void TypingJustALineNumberDeletesIt()
    {
        Assert.Equal("10 REM A\n", Basic.Run("10 REM A", "20 REM B", "20", "LIST"));
    }

    [Fact]
    public void NewClearsProgram()
    {
        Assert.Equal("", Basic.Run("10 REM A", "NEW", "LIST"));
    }

    [Fact]
    public void RenumberRewritesTargets()
    {
        var output = Basic.Run(
            "1 GOTO 3",
            "2 PRINT \"X\"",
            "3 IF A=0 THEN 2 ELSE 1",
            "4 ON A GOTO 1,2,3:GOSUB 2",
            "5 PRINT \"GOTO 1\" : REM GOTO 3",
            "RENUMBER",
            "LIST");
        Assert.Equal(Basic.Lines(
            "10 GOTO 30",
            "20 PRINT \"X\"",
            "30 IF A=0 THEN 20 ELSE 10",
            "40 ON A GOTO 10,20,30:GOSUB 20",
            "50 PRINT \"GOTO 1\" : REM GOTO 3"), output);
    }

    [Fact]
    public void RenumberWithArguments()
    {
        var output = Basic.Run("5 REM", "7 GOTO 5", "RENUMBER 100,5", "LIST");
        Assert.Equal("100 REM\n105 GOTO 100\n", output);
    }

    [Fact]
    public void RenumberFromLeavesEarlierLinesAlone()
    {
        var output = Basic.Run("10 REM", "20 REM", "30 GOTO 20", "RENUMBER 100,10,20", "LIST");
        Assert.Equal("10 REM\n100 REM\n110 GOTO 100\n", output);
    }

    [Fact]
    public void RenumberRefusesToReorderLines()
    {
        var output = Basic.Run("10 REM", "20 REM", "30 REM", "RENUMBER 5,10,20");
        Assert.Contains("?ILLEGAL QUANTITY", output);
    }

    [Fact]
    public void DeleteRange()
    {
        Assert.Equal("10 REM A\n40 REM D\n", Basic.Run("10 REM A", "20 REM B", "30 REM C", "40 REM D", "DELETE 20-30", "LIST"));
    }

    [Fact]
    public void FindListsMatchingLines()
    {
        Assert.Equal("20 PRINT \"Needle\"\n", Basic.Run("10 REM hay", "20 PRINT \"Needle\"", "FIND \"needle\""));
    }

    [Fact]
    public void TraceShowsLineNumbers()
    {
        var output = Basic.Run("10 PRINT \"A\"", "20 PRINT \"B\"", "TRACE ON", "RUN", "TRACE OFF", "RUN");
        Assert.Equal("[10]A\n[20]B\nA\nB\n", output);
    }

    [Fact]
    public void AutoNumbersLines()
    {
        var console = new TestConsole(new[] { "REM ONE", "REM TWO", "", "LIST", });
        var interp = new Interpreter(console, new MemoryFileSystem());
        var repl = new C64Basic.Core.Repl(interp, console);
        interp.ProcessLine("AUTO 100,5");
        repl.Run();
        Assert.Contains("100 REM ONE\n105 REM TWO\n", console.Output);
        Assert.Contains("100 ", console.Output);
    }
}

public class FileTests
{
    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var (_, interp, fs) = Basic.Session("10 PRINT \"HI\"", "20 END", "SAVE \"prog\"");
        Assert.Equal(new[] { "10 PRINT \"HI\"", "20 END" }, fs.Files["prog.bas"]);

        interp.ProcessLine("NEW");
        interp.ProcessLine("LOAD \"prog\"");
        Assert.Equal(new[] { "10 PRINT \"HI\"", "20 END" }, interp.Listing());
    }

    [Fact]
    public void LoadMissingFile()
    {
        Assert.Equal("SEARCHING FOR nope\n?FILE NOT FOUND  ERROR\n", Basic.Run("LOAD \"nope\""));
    }

    [Fact]
    public void VerifyDetectsDifference()
    {
        var (_, interp, _) = Basic.Session("10 REM", "SAVE \"p\"");
        interp.ProcessLine("20 REM");
        var console = new TestConsole();
        // A fresh interpreter sharing nothing proves VERIFY compares against the file text.
        var fs = new MemoryFileSystem();
        var other = new Interpreter(console, fs);
        fs.Files["p.bas"] = new[] { "10 REM" };
        other.ProcessLine("10 REM");
        other.ProcessLine("VERIFY \"p\"");
        Assert.Equal("SEARCHING FOR p\nVERIFYING\nOK\n", console.Output);
        other.ProcessLine("20 REM");
        other.ProcessLine("VERIFY \"p\"");
        Assert.Equal("SEARCHING FOR p\nVERIFYING\nOK\nSEARCHING FOR p\nVERIFYING\n?VERIFY  ERROR\n", console.Output);
    }
}

public class NumberFormatTests
{
    [Theory]
    [InlineData(0, " 0")]
    [InlineData(1, " 1")]
    [InlineData(-1, "-1")]
    [InlineData(3.14159265358979, " 3.14159265")]
    [InlineData(100, " 100")]
    [InlineData(0.5, " .5")]
    [InlineData(999999999, " 999999999")]
    [InlineData(1e10, " 1E+10")]
    [InlineData(1.5e-5, " 1.5E-05")]
    [InlineData(1234567890, " 1.23456789E+09")]
    [InlineData(1.7e38, " 1.7E+38")]
    public void Formats(double value, string expected)
    {
        Assert.Equal(expected, NumberFormat.Format(value));
    }
}

public class LexerTests
{
    [Fact]
    public void VariableContainingKeywordIsSplit()
    {
        // TOTAL is read as TO + TAL on a real C64.
        var lexer = new C64Basic.Core.Lexing.Lexer(strict: true);
        var toks = lexer.Lex("TOTAL");
        Assert.Equal("TO", toks[0].Text);
        Assert.Equal(C64Basic.Core.Lexing.TokKind.Keyword, toks[0].Kind);
        Assert.Equal("TAL", toks[1].Text);
    }

    [Fact]
    public void RemAndDataKeepRawText()
    {
        var lexer = new C64Basic.Core.Lexing.Lexer(strict: false);
        var toks = lexer.Lex("REM GOTO 10 : PRINT");
        Assert.Equal(3, toks.Count); // REM, raw text, EOL
        Assert.Equal(C64Basic.Core.Lexing.TokKind.Raw, toks[1].Kind);
    }

    [Fact]
    public void VariableNamesAreSignificantToTwoCharacters()
    {
        Assert.Equal(" 5 \n", Basic.Run("ABC=5:PRINT ABD"));
    }
}
