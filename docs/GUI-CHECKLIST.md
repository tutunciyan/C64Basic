# GUI manual test checklist

The automated tests cover the screen editor, keyboard buffer and key matrix, and CI starts the window with SDL's dummy
drivers. These need a real display, keyboard and speakers. Run `dotnet run --project src/C64Basic.Gui` (or a release build)
and tick them off. Note the OS and the result.

## Window
- [ ] Opens at 1152x816 with a blue screen, light-blue border and the banner; cursor blinks at the prompt.
- [ ] Resizing keeps the picture sharp (nearest-neighbour) and letterboxed at the right aspect.
- [ ] F11 and Alt+Enter toggle full screen; closing the window exits cleanly.
- [ ] F9 toggles "(warp)" in the title: `FOR I=1 TO 20000:NEXT` is instant in warp, about 13 s otherwise.
- [ ] F12 writes `c64-<time>.bmp` that opens in a viewer.

## Keyboard
- [ ] Letters, digits and punctuation type; unshifted letters show as capitals, Shift+letter as graphics symbols.
- [ ] Shift+Alt switches to the lower-case set and back; `PRINT "Hello"` then reads as mixed case. Type `"` then Ctrl+2: a reverse symbol appears and the colour does not change.
- [ ] Backspace deletes, Insert inserts, Home homes, Shift+Home clears, arrow keys move the cursor.
- [ ] Move the cursor up onto a `LIST` line, edit it, press Enter: the new line replaces the old one.
- [ ] A line of 60+ characters wraps and still runs.
- [ ] F1-F8: `10 GET A$:IF A$="" THEN 10`, `20 PRINT ASC(A$):GOTO 10`, RUN, press each key: 133, 137, 134, 138, 135, 139, 136, 140.
- [ ] Ctrl+1..8 and Alt+1..8 change the text colour; Ctrl+9 / Ctrl+0 switch reverse video.
- [ ] Esc breaks a running program (`10 GOTO 10`) with `BREAK IN 10`; CONT resumes it.
- [ ] Holding a key does not flood the buffer (ten characters at most while a program is busy).
- [ ] Key matrix: `10 PRINT PEEK(197):GOTO 10` shows 64 idle and a changing code per key; Shift alone stays 64 and
      `PRINT PEEK(653)` shows 1 while it is held.

## Joystick
- [ ] `LOAD "samples/joystick"` then `RUN` (samples/joystick.bas): numpad 8/2/4/6 move the sprite, 0 quits. Releasing a key stops that direction.
- [ ] Switching to another window while holding a key does not leave it stuck.
- [ ] Pause moves the numpad joystick to port 1 (the title says so): `PRINT PEEK(56321)` changes while holding numpad 8 and `PEEK(56320)` does not. `--joy 1` starts that way.
- [ ] A game controller (plug it in after the window is open too): D-pad and left stick move the sprite in samples/joystick.bas, A/B/X/Y fire. A second controller drives port 1. Unplugging one releases it.

## Graphics and sound
- [ ] samples/sprite.bas: a yellow ball bounces off all four edges, including past X=255.
- [ ] samples/sid.bas: a rising C major scale, no clicks or hangs, silent afterwards.
- [ ] `POKE 53280,2` changes the border at once, `POKE 53281,0` the background.
- [ ] `POKE 1024,1:POKE 55296,5` draws a white-ish A in the top-left corner.
- [ ] samples/sysdemo.bas fills the screen with a pattern and returns to the prompt.

- [ ] `--sid 8580` starts without error; a filtered sound (`POKE 54295,1:POKE 54296,31+16`, sweep `POKE 54294`) is brighter than with the default 6581.
- [ ] A raster-bar machine-code demo (colour changed by a raster IRQ) shows steady, non-flickering bars and the BASIC prompt stays responsive.

## Clipboard
- [ ] Copy a short BASIC program from a text editor, press Ctrl+V (and again with Shift+Insert): it is typed line by line and `RUN` works. Tabs and accented characters do not break it.
- [ ] Ctrl+C after `PRINT "HELLO"` puts the screen text on the clipboard.

## Machine state
- [ ] Run samples/sprite.bas, press Ctrl+S mid-run (the title says "state saved"), let it finish, press Ctrl+L: the ball is back where it was and keeps bouncing.
- [ ] Ctrl+S at the prompt, type `NEW` and `POKE 53280,0`, Ctrl+L: the program, the border colour and the screen are back.
- [ ] `--resume` starts with the saved screen; dropping the `.sav` file on the window loads it too.
- [ ] Ctrl+L with no file says "no saved state" and changes nothing.

## Files
- [ ] `dotnet run --project src/C64Basic.Gui -- --disk test.d64` creates the image; `SAVE "X",8` then `LOAD "$",8`, `LIST` shows it.
- [ ] Dropping a `.d64` onto the window mounts it and lists the directory; dropping a `.bas` or BASIC `.prg` loads and runs it.
