namespace C64Basic.Core.Runtime;

/// <summary>The classic BASIC V2 error numbers (the value of ERR on a real C64).</summary>
public enum ErrorCode
{
    TooManyFiles = 1, FileOpen, FileNotOpen, FileNotFound, DeviceNotPresent, NotInputFile,
    NotOutputFile, MissingFileName, IllegalDevice, NextWithoutFor, Syntax, ReturnWithoutGosub,
    OutOfData, IllegalQuantity, Overflow, OutOfMemory, UndefdStatement, BadSubscript,
    RedimdArray, DivisionByZero, IllegalDirect, TypeMismatch, StringTooLong, FileData,
    FormulaTooComplex, CantContinue, UndefdFunction, Verify, Load,
}

public static class ErrorNames
{
    static readonly string[] Names =
    {
        "", "TOO MANY FILES", "FILE OPEN", "FILE NOT OPEN", "FILE NOT FOUND", "DEVICE NOT PRESENT",
        "NOT INPUT FILE", "NOT OUTPUT FILE", "MISSING FILE NAME", "ILLEGAL DEVICE NUMBER",
        "NEXT WITHOUT FOR", "SYNTAX", "RETURN WITHOUT GOSUB", "OUT OF DATA", "ILLEGAL QUANTITY",
        "OVERFLOW", "OUT OF MEMORY", "UNDEF'D STATEMENT", "BAD SUBSCRIPT", "REDIM'D ARRAY",
        "DIVISION BY ZERO", "ILLEGAL DIRECT", "TYPE MISMATCH", "STRING TOO LONG", "FILE DATA",
        "FORMULA TOO COMPLEX", "CAN'T CONTINUE", "UNDEF'D FUNCTION", "VERIFY", "LOAD",
    };

    public static string Name(ErrorCode code) => Names[(int)code];
}

/// <summary>A BASIC error. <see cref="Column"/> is a 0-based offset into the program line, or -1 if unknown.</summary>
public sealed class BasicException : Exception
{
    public ErrorCode Code { get; }
    public int Column { get; }

    public BasicException(ErrorCode code, int column = -1) : base(ErrorNames.Name(code))
    {
        Code = code;
        Column = column;
    }
}

/// <summary>Thrown when the console input stream ends while the interpreter is waiting for a line.</summary>
public sealed class InputEndedException : Exception { }
