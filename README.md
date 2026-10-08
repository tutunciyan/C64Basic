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

## Files, WAIT, SYS and console codes

- `OPEN lf,dev,sa,"name[,S,W|R|A]"`, `CLOSE`, `PRINT#`, `INPUT#`, `GET#`, `CMD` and `ST` work on devices 0 (keyboard),
  3 (screen) and 8-11 (disk). Disk files are plain text with CR-separated records, kept in memory until `CLOSE`.
  `OPEN 15,8,15` gives a command channel that always reads back `00, OK,00,00`. Input items end at a comma, colon or CR.
- `WAIT 198,n` waits for a key (works with a following `GET`); any other `WAIT` polls `PEEK` memory until RUN/STOP.
- `SYS` supports 58692 (clear screen), 64738 (reset), and 65490 (CHROUT, character in `POKE 780`). Anything else
  raises `?ILLEGAL QUANTITY`, since there is no 6502.
- The console front end handles PETSCII home, cursor up/down/left/right, reverse on/off and the 16 colour codes.

## Not implemented

Tape (device 1) and printer (device 4) raise `?DEVICE NOT PRESENT`. `USR` raises `?ILLEGAL QUANTITY`.
Relative and program files, disk commands such as scratch, and `POKE` effects other than 646 (text colour) and
53281 (background) are missing. `LOAD`/`SAVE`/`VERIFY` read and write plain-text `.bas` files rather than tape or disk images.

Known deviation: a `FOR` loop's resume point is a statement index, which is exact for all cases including
`GOSUB` inside an `IF` clause, but a variable named like an extension keyword (`ELSE`, `FIND`, `AUTO`, ...) is
split by the tokenizer unless you use `--strict`.
