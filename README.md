# C64Basic

A Commodore 64 **BASIC V2** interpreter written in C# (.NET 10), with a few editing and tooling extensions.
It interprets the language; it does not emulate a 6502 or the C64 ROM.

```
dotnet run --project src/C64Basic.Console                      # interactive READY. prompt
dotnet run --project src/C64Basic.Console -- samples/sieve.bas # run a program and exit
dotnet run --project src/C64Basic.Console -- --strict          # stock V2 only
dotnet test
```

Options: `--run <file>`, `--strict`, `--plain` (don't switch the terminal to C64 colours), `--help`.
Press Ctrl+C to act as RUN/STOP.

## Layout

| Path | Contents |
|---|---|
| `src/C64Basic.Core` | the interpreter library: `Lexing`, `Parsing`, `Runtime`, `Editor`, `IO`, `Repl` |
| `src/C64Basic.Console` | terminal front end (`IConsoleDevice` over `System.Console`) |
| `tests/C64Basic.Tests` | xUnit tests; `Harness.cs` has a scripted console and in-memory file system |
| `samples/` | example programs |

The core has no console dependency. A GUI only needs to implement `IConsoleDevice` and `IFileSystem`.

## What is faithful to V2

- Keywords are recognised anywhere, so `FORI=1TO10` works and a variable named `TOTAL` is read as `TO`+`TAL`.
- Names are significant to two characters; `$` strings and `%` 16-bit integers.
- Operator precedence, `-1` for true, 16-bit `AND`/`OR`/`NOT`.
- PRINT number formatting (`.333333333`, `1E+09`), 10-column comma zones, `TAB`, `SPC`.
- The classic error messages (`?SYNTAX  ERROR IN 40`). Syntax errors surface only when execution reaches them.
- 40-bit float range: `?OVERFLOW` above about 1.7E38.
- `FOR` bodies always run once, because the limit is tested at `NEXT`.
- `TI`, `TI$`, `ST`, `PEEK`/`POKE` on a 64K array, `DATA`/`READ`/`RESTORE`, `DEF FN`, `ON..GOTO/GOSUB`, `CONT`.

## Extensions (all off with `--strict`)

| Command | Effect |
|---|---|
| `RENUMBER [start[,step[,from]]]` | renumber lines and rewrite GOTO/GOSUB/THEN/ELSE/RUN/ON targets |
| `AUTO [start[,step]]` | prompt with line numbers; a blank line ends it |
| `TRACE [ON\|OFF]` | print `[line]` as each line runs |
| `DELETE a-b` | delete a range of lines |
| `FIND "text"` | list lines containing text |
| `IF .. THEN .. ELSE ..` | |
| lines up to 255 characters | stock limit is 80 |
| syntax errors show the source line with a `^` | |

`LIST` normalises case and expands `?` to `PRINT`.

## Not implemented

`OPEN`, `CLOSE`, `CMD`, `WAIT` and `SYS` parse but raise `?DEVICE NOT PRESENT`. `USR` raises `?ILLEGAL QUANTITY`.
There is no `PRINT#`/`INPUT#`/`GET#`, no cursor-control PETSCII codes (except `CHR$(147)` to clear the screen),
and `POKE` only has an effect on 646 (text colour) and 53281 (background) in the console front end.
`LOAD`/`SAVE`/`VERIFY` read and write plain-text `.bas` files rather than tape or disk images.

Known deviation: a `FOR` loop's resume point is a statement index, which is exact for all cases including
`GOSUB` inside an `IF` clause, but a variable named like an extension keyword (`ELSE`, `FIND`, `AUTO`, ...) is
split by the tokenizer unless you use `--strict`.
