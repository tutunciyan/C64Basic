# GUI manual test checklist

The automated tests cover the machine, the keyboard matrix and the window's helpers. These need a real display, keyboard, speakers and
the ROM dumps in `roms/` (and, for the game items, a disk image). Run `dotnet run --project src/C64Basic.Gui` (or a release build)
and tick them off. Note the OS and the result.

## Window
- [ ] Opens at 1152x816 with the toolbar on top; after about three seconds the blue screen, light-blue border and banner appear; the cursor blinks at the prompt.
- [ ] Without the ROM dumps it prints which files are needed and exits with an error (try `--rom-dir` on an empty folder).
- [ ] Resizing keeps the picture sharp (nearest-neighbour) and letterboxed at the right aspect.
- [ ] F11 and Alt+Enter toggle full screen; closing the window exits cleanly.
- [ ] F9 toggles warp: a `FOR I=1 TO 20000:NEXT` is instant in warp, about 13 s otherwise. The title says "(warp)".
- [ ] F10 resets to the banner. F12 writes `c64-<time>.bmp` that opens in a viewer.
- [ ] Scroll Lock pauses and shows the registers in the title; pressing it again resumes.

## Keyboard
- [ ] Letters, digits and punctuation type; unshifted letters show as capitals, Shift+letter as graphics symbols.
- [ ] Shift+Alt switches to the lower-case set and back. Type `"` then Ctrl+2: a reverse symbol appears and the colour does not change.
- [ ] Backspace deletes, Insert inserts, Home homes, Shift+Home clears, arrow keys move the cursor.
- [ ] Move the cursor up onto a `LIST` line, edit it, press Enter: the new line replaces the old one.
- [ ] F1-F8: `10 GET A$:IF A$="" THEN 10`, `20 PRINT ASC(A$):GOTO 10`, RUN, press each key: 133, 137, 134, 138, 135, 139, 136, 140.
- [ ] Ctrl+1..8 and Alt+1..8 change the text colour; Ctrl+9 / Ctrl+0 switch reverse video.
- [ ] Esc breaks a running program (`10 GOTO 10`) with `BREAK IN 10`; CONT resumes it. Page Down is RESTORE (RUN/STOP+RESTORE resets the screen).
- [ ] `10 PRINT PEEK(197):GOTO 10` shows 64 idle and a changing code per key; Shift alone stays 64 and `PRINT PEEK(653)` shows 1 while it is held.

## Joystick
- [ ] Ctrl+J (or the JOY button) cycles the cursor keys: off, port 1, port 2 (the button shows the port). With a port on, the cursor keys and Space no longer move the cursor, and `PRINT PEEK(56321)` / `PEEK(56320)` change on that port. `--joy-keys 2` starts that way.
- [ ] The numpad is a joystick (8/2/4/6, 0 = fire) on port 2; Pause moves it to port 1 (the title says so). `--joy 1` starts that way.
- [ ] Switching to another window while holding a key does not leave it stuck.
- [ ] A game controller (plug it in after the window is open too): D-pad and left stick move, A/B/X/Y fire. A second controller drives port 1. Unplugging one releases it.

## Paddles and light pen
- [ ] `POKE 56320,128:PRINT PEEK(54297)` follows the mouse: 0 at the left edge of the window, about 255 at the right; `PEEK(54298)` follows the vertical position. After `--joy 1` (or the Pause key) use `POKE 56320,64`.
- [ ] While `POKE 56320,128` is set, holding the left mouse button makes `PEEK(56320) AND 4` read 0 (fire A); the right button clears bit 3. Releasing the window focus releases both.
- [ ] `--lightpen`: hold the left mouse button over the middle of the picture, then `PRINT PEEK(53267),PEEK(53268)` shows roughly 80 and 150 (X in 2-pixel units, raster line); `PEEK(56321) AND 16` reads 0 while the button is down.

## Graphics and sound
- [ ] `POKE 53280,2` changes the border at once, `POKE 53281,0` the background.
- [ ] `POKE 1024,1:POKE 55296,5` draws a white-ish A in the top-left corner.
- [ ] A game that multiplexes sprites (for example International Karate Plus) draws whole fighters, not garbled ones.
- [ ] Game music plays without breaking up, at about 40 % volume; Ctrl+Up / Ctrl+Down change the level, Ctrl+M mutes, the speaker buttons do the same.
- [ ] `--sid 8580` starts without error; a filtered sound is brighter than with the default 6581.

## Disks, tapes and cartridges
- [ ] `--disk test.d64` creates the image; `SAVE "X",8` then `LOAD "$",8`, `LIST` shows it, and the file is saved back to disk.
- [ ] START (Ctrl+Enter or the play-arrow button) resets and runs the first program of the disk; `--start` does it at launch; with no disk it says so in the title.
- [ ] OPEN (Ctrl+O) opens the system file dialog; a picked `.d64`, `.g64`, `.t64` or `.prg` resets and starts it, a `.crt` resets into the cartridge, a `.sav` loads the state. Dropping the file on the window does the same.
- [ ] Ctrl+N / Ctrl+B swap in the next / previous disk image in the folder. The title shows the drive's track while it turns.
- [ ] `--tape x.tap`: Ctrl+T lets go of PLAY and presses it again, Ctrl+R rewinds; `LOAD` loads from the tape.

## Clipboard and selection
- [ ] Copy a short BASIC program from a text editor, press Ctrl+V (and again with Shift+Insert, and with a middle click): it is typed line by line and `RUN` works.
- [ ] Shift + left drag picks text (drawn inverted) and copies it when the button is let go; Ctrl+C copies the selection, or the whole screen when nothing is picked.

## Machine state
- [ ] Ctrl+S mid-program (the title says "state saved"), let it run on, press Ctrl+L: the machine is back where it was.
- [ ] Ctrl+L with no file says "no saved state" and changes nothing. `--resume` starts from the saved state; dropping the `.sav` file on the window loads it too.
