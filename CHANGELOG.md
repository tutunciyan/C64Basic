# 0.3.0

**ROM mode: the real C64 ROMs and a real 1541**
- The 6502 core runs on any memory map (`ICpuMemory`) and has a native mode with real vectors, interrupt lines, the SO pin and the
  cycle of every bus access; `Lockstep` keeps processors with different clocks (C64 985248 Hz, 1541 1 MHz) in step.
- The 1541's computer: 6502 at 1 MHz, 2 KB RAM, the DOS ROM and two 6522 VIAs (timers, ports, handshake pins, interrupts).
- The serial bus: CIA 2 and the drive's VIA 1 on three wired-AND lines, including the drive's automatic ATN acknowledge. The real DOS ROM boots
  and answers on it (tested: status string, device number jumpers, error channel). ROM dumps are found in a folder by name or size and checked.
- Disk mechanics: the stepper (half-tracks, a stop at each edge), the motor and a read channel that turns the GCR bit stream of a G64 (or a
  D64 encoded as one) into data bytes, SYNC and byte-ready (V flag and CA1) at the speed of each zone. The real DOS reads directories and
  multi-sector files through it, and a G64 game image's directory.
- `RomMachine`: the real BASIC and KERNAL ROMs on the 6502 (banked by the processor port, real IRQ, keyboard scan, serial routines) beside a
  real 1541, in lockstep ordered by the cycle of each processor's next bus access. It boots to READY, runs BASIC, lists a directory and loads
  programs over the serial bus, and a game with a drive-code fast loader (a G64 of 1943) loads and runs. Released serial lines rise a
  microsecond late, as on a real cable; fast loaders' timing windows need that. Swapping a disk flickers the write-protect sensor so the
  DOS notices.
- The 1541 writes: the write head (VIA 2 CB2 low) puts port A on the track at the speed of the zone, so the real DOS can `SAVE`, scratch,
  validate and format. A mounted D64 file is saved back after a write; a G64 stays as it was (changes live in memory).
- The unstable undocumented opcodes (XAA, LAX #, AHX, TAS, SHX, SHY, LAS) run in ROM mode, for copy protection that uses them.
- ROM mode saves and restores the whole machine (Ctrl+S / Ctrl+L, `--state`, `--resume`), exactly: a state from the middle of a fast load
  runs on to the same result. `--iec-rise <us>` tunes the serial bus rise time; the window title shows the drive (track, writing).

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
