# C64Basic

A Commodore 64 **BASIC V2** interpreter written in C# (.NET 10), with a few editing and tooling extensions.
It interprets the language, and `SYS`/`USR` run machine code on a built-in 6502 core. There is no C64 ROM image: KERNAL and BASIC entry points are emulated in C#.

```
dotnet run --project src/C64Basic.Console                      # interactive READY. prompt
dotnet run --project src/C64Basic.Console -- samples/sieve.bas # run a program and exit
dotnet run --project src/C64Basic.Console -- --strict          # stock V2 only
dotnet test
```

Options: `--run <file>`, `--strict`, `--plain` (plain text stream: no emulated screen), `--fast` (don't slow execution to C64 speed), `--width <n>` (emulated screen columns, centred with a border; 0 = terminal width; default 40, like a real C64), `--disk [n=]<file.d64>` (mount a disk image as device n, default 8; created blank if missing), `--tape <file.t64>`, `--help`.
Press Ctrl+C to act as RUN/STOP.

## Layout

| Path | Contents |
|---|---|
| `src/C64Basic.Core` | the interpreter library: `Lexing`, `Parsing`, `Runtime`, `Editor`, `IO`, `Machine` (memory bus, 6502 CPU, VIC-II, SID, CIA, colour RAM), `Repl` |
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
  3 (screen), 4/5 (printer), 1 (tape) and 8-11 (disk). Input items end at a comma, colon or CR. Without a mounted image, tape
  and disk are the host's working directory: sequential files are plain text with CR-separated records, kept in memory until
  `CLOSE`. The printer appends to `PRINTER.TXT`.
- **Disk drives** (`Core/Disk`): `--disk` mounts a real 35-track `.d64` image (block allocation map, directory chain, sector
  chains with interleave; every change is written back to the file). `OPEN` on it creates PETSCII `SEQ` files.
  `LOAD "$",8` loads the directory as a BASIC program (`LOAD "$0:A*",8` filters it), `LOAD "N",8,1` loads machine code at the
  file's own address, `SAVE "@0:N",8` replaces a file (without `@` an existing name is DOS error 63), and wildcards
  work (`LOAD "GA*",8`). Programs on disk are tokenized PRG files (`PrgFormat`); on the host directory `SAVE "x"` still writes
  readable `x.bas` source and `SAVE "x.prg"` writes tokenized bytes. LOAD/SAVE/VERIFY print the `SEARCHING FOR`/`LOADING`/
  `SAVING`/`OK` messages in direct mode, and `PRESS PLAY ON TAPE` etc. for device 1.
- **Command channel** (`OPEN 15,8,15`): `PRINT#15,"S:name"` (scratch, wildcards), `R:new=old`, `N:name,id` (format), `V` (validate),
  `I`, `C:new=a,b` (copy/concatenate). `INPUT#15,E,E$,T,S` reads `00, OK,00,00` style status (`62, FILE NOT FOUND`, `63, FILE EXISTS`,
  `72, DISK FULL`, ...). A failed OPEN or LOAD of a missing file still raises `?FILE NOT FOUND`; other DOS errors only show in the status.
- **Tape**: `--tape` mounts a `.t64` archive as device 1 (`LOAD "",1` loads the next program).
- The KERNAL `LOAD` ($FFD5) and `SAVE` ($FFD8) calls work from machine code through `SETLFS`/`SETNAM`.
- `WAIT 198,n` waits for a key (works with a following `GET`); any other `WAIT` polls `PEEK` memory until RUN/STOP.
- `SYS addr` runs machine code on a 6502 core (all documented opcodes with cycle counts, decimal mode, RMW dummy writes;
  it passes Klaus Dormann's functional test). A, X, Y and P are loaded from and stored to 780-783, as BASIC does, and
  the program ends at the first `BRK` (the default KERNAL vector returns to READY). `USR(x)` jumps through the vector at
  785 with `x` in the floating-point accumulator ($61-$66) and takes the result from it. Undocumented opcodes, and
  jumps into KERNAL/BASIC ROM addresses without an emulation, raise `?ILLEGAL QUANTITY`.
- Emulated ROM entry points: CHROUT $FFD2, CHRIN $FFCF, GETIN $FFE4, STOP $FFE1, PLOT $FFF0, READST $FFB7, SETLFS, SETNAM,
  SCNKEY/CLRCHN/CLALL (no-ops), clear screen $E544, home $E566, reset $FCE2, BASIC warm start $A474/$A483/$E37B (ends
  the SYS), and the IRQ tail $EA31/$EA7E/$EA81. Interrupts are delivered while machine code runs: the CIA 1 timer and VIC-II
  raster IRQs go through the RAM vector at 788/789 (or $FFFE/$FFFF once the KERNAL is banked out with `POKE 1`), so
  raster-interrupt programs work. NMI, the KERNAL OPEN/CLOSE/CHKIN calls and the BASIC ROM's floating-point routines are not
  emulated. Execution is paced to 985 kHz unless `--fast`; Ctrl+C stops a runaway routine.
- In a terminal the console front end shows an emulated 40x25 C64 screen with border (true-colour ANSI): PETSCII home,
  cursor keys, reverse and the 16 colour codes, scrolling, and `POKE` to screen RAM (1024), colour RAM (55296),
  cursor row/column (214/211), text colour (646), border (53280) and background (53281). Typed letters show as
  upper case, as on a C64. Execution is paced at roughly 1500 statements/s so animations look right; use `--fast` to
  disable. PETSCII graphics from `CHR$` are drawn with Unicode box/block characters.
- `PEEK(53248..57343)` reads the character ROM after `POKE 1,PEEK(1) AND 251` (upper-case/graphics set; screen codes 0-63
  are exact, a few graphics are drawn, and the rest read as blank). `ASC` of a typed letter gives its upper-case PETSCII code.
  Programs that rely on 40-column line wrapping (e.g. `samples/banner.bas`) run correctly, since the screen is 40 columns by default.

## VIC-II, SID and CIA (core only)

`interp.Bus` models the VIC-II and SID registers. `Bus.Vic.Render(uint[])` draws a 384x272 ARGB frame: text,
multicolour and extended-colour text, hires and multicolour bitmaps, custom character sets, the VIC bank from
`$DD00`, and eight sprites (expansion, multicolour, priority, sprite-sprite and sprite-background collisions).
`$D012`/`$D011` give a raster line that advances at 50 Hz and the raster-compare flag is set in `$D019`.
`Bus.Sound.Render(short[], sampleRate)` produces mono audio: three voices, four waveforms, ring modulation,
sync, ADSR, a state-variable filter and volume. `IVideoDevice` and `IAudioDevice` are the interfaces a front end
implements to show and play these; the terminal front end does not, so sprites and sound only come out through a
host that calls them (a GUI is planned). A SID frequency register tops out near 3.8 kHz, as on a real chip.

`Bus.Cia1`/`Cia2` are 6526 chips with two ports, two timers (also chained), time of day with the hours latch, and an
interrupt register; they run from `Bus.Seconds`. CIA 1 reads a host-supplied `IInputDevice` through the real keyboard
matrix and joystick ports (`PEEK(56320)` for joystick 2), CIA 2 port A picks the VIC bank, and CIA 1 timer A is
the 60 Hz system interrupt (`InterruptPending`, for the CPU to poll). With an `IInputDevice` set on `Bus.Input`,
`PEEK(197)` gives the KERNAL key index (64 = none) and `PEEK(653)` the shift/C=/CTRL flags. The jiffy clock at
160-162 and `TI` are the same clock, and `POKE` to 160-162 sets `TI`. The PB6/PB7 timer outputs, the serial
shift register and TOD alarms are not modelled, and the terminal front end does not supply input.

## Not implemented

`USR` without a vector raises `?ILLEGAL QUANTITY`. Relative (`REL`) files, direct-access block commands (`U1`, `B-R`, ...),
`.tap` pulse images and DOS errors beyond those listed are missing, as are any `POKE`/`PEEK` hardware registers not listed
above. `PEEK` of screen RAM, colour RAM and the cursor (214/211) sees printed text only in the emulated screen, not with `--plain`.
`LOAD` without a device number reads from device 8 (a real C64 defaults to tape). A directory loaded with `LOAD "$"` keeps its
lines in file order, so several files with the same block count each show up, but it should not be edited or run.

Known deviation: a `FOR` loop's resume point is a statement index, which is exact for all cases including
`GOSUB` inside an `IF` clause, but a variable named like an extension keyword (`ELSE`, `FIND`, `AUTO`, ...) is
split by the tokenizer unless you use `--strict`.
