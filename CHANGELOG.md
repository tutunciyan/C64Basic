# Changes since v0.1.0

**Character set and screen**
- Built-in character set with a lower-case set, `$D018` switching, `--chargen` for a real ROM dump, quote mode.

**Real C64 behaviour**
- KERNAL entry points and BASIC floating-point routines callable from machine code, NMI and RESTORE, `--strict` stock behaviour.

**Disk and tape**
- `.d64` images (relative files, block commands, `M-R`/`M-W`), `.t64`, `.tap`; DOS errors 60, 63 and 64; `--disk`, `--tape`.

**Input and output**
- Game controllers and a switchable joystick port, clipboard paste/copy, whole-machine save and restore (Ctrl+S / Ctrl+L), `--pixels`
  (the real picture in a terminal) with a live key matrix, the mouse as a paddle, `--plain` keeps a hidden screen.

**Accuracy**
- VIC-II drawn per raster line: raster tricks, border opening, sprite multiplexing, exact raster IRQs, bad-line and per-sprite cycle
  stealing, light pen. Machine code runs cycle-exact with interrupts after every instruction.
- SID 6581/8580 model (`--sid`), combined waveforms, filter curves.
- CIA timer outputs on PB6/PB7, shift register, TOD alarm, CNT and FLAG pins, user port.

See the README for the details and the known limits.
