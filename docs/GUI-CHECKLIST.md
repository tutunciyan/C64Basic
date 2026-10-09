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

## Paddles
- [ ] `POKE 56320,128:PRINT PEEK(54297)` follows the mouse: 0 at the left edge of the window, about 255 at the right; `PEEK(54298)` follows the vertical position. After `--joy 1` (or the Pause key) use `POKE 56320,64`.
- [ ] While `POKE 56320,128` is set, holding the left mouse button makes `PEEK(56320) AND 4` read 0 (fire A); the right button clears bit 3. Releasing the window focus releases both.

- [ ] `--lightpen`: hold the left mouse button over the middle of the picture, then `PRINT PEEK(53267),PEEK(53268)` shows roughly 80 and 150 (X in 2-pixel units, raster line); `PEEK(56321) AND 16` reads 0 while the button is down.

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

## ROM mode (`--rom-dir roms`, needs the ROM dumps)
- [ ] The window opens as "C64 (ROM mode)" and shows the real boot: the RAM test (a blank screen for about 2 s), then the banner and `READY.` with a blinking cursor.
- [ ] Typing works through the real keyboard scan: letters, digits, Shift+letter graphics, Shift+Alt switches the character set, Ctrl+1..8 and Alt+1..8 change the colour, the cursor keys, Home, Backspace and Insert.
- [ ] `--disk some.d64`: `LOAD"$",8` then `LIST` shows the directory; `LOAD"NAME",8` and `RUN` run a program. Dropping another disk image on the window mounts it, and a second `LOAD"$",8` shows the new directory.
- [ ] A game with a fast loader (`LOAD"*",8,1`) loads and runs; the title shows nothing odd while it does.
- [ ] F9 toggles warp (title says so) and a loading game gets noticeably faster; F10 resets to the banner with the disk still mounted.
- [ ] Esc is RUN/STOP, Page Down is RESTORE: Esc+Page Down breaks out of `10 GOTO 10`.
- [ ] Ctrl+V pastes a line of BASIC; Ctrl+C copies the screen text.
- [ ] Numpad is the joystick; `PRINT PEEK(56320)` shows 127 when idle on port 2 and a changed value while a numpad key is held.
- [ ] `--rom-dir` with a missing folder or missing ROMs names what is missing and exits.
- [ ] `--disk new.d64` (a file that does not exist) makes a blank disk; `10 PRINT "HI"`, `SAVE "HI",8` and the title says "disk saved"; a second run with the same `--disk` lists `HI`.
- [ ] A `.g64` stays untouched after a `SAVE` (the title says it was not saved).
- [ ] Ctrl+S in the middle of loading a game and Ctrl+L after it carries on loading; `--resume` starts from the saved state.
- [ ] The title shows `1541: track N` while the drive spins and `writing` while it saves.
- [ ] ROM mode, several `.d64` files in one folder (`--disk game-1.d64`): Ctrl+N mounts the next image in name order (the title says which), Ctrl+B the previous one, both wrap round; `LOAD"$",8` shows the other directory.
- [ ] ROM mode, Scroll Lock pauses: the title shows `paused` with PC, A, X, Y, SP, the flags, the raster line and the drive's PC, the picture and sound stand still; Scroll Lock again carries on where it was.
- [ ] ROM mode, `--tape some.tap` (or drop a `.tap`): `LOAD` finds the program without "PRESS PLAY ON TAPE", the screen goes blank with the loading border stripes while it plays; Ctrl+T lets go of PLAY (the KERNAL then asks for it) and presses it again, Ctrl+R rewinds.
- [ ] ROM mode, `--disk 8=a.d64 --disk 9=b.d64`: `LOAD"$",9` lists the second disk, and the title names `1541 #9` while it turns.
- [ ] ROM mode, `--cart game.crt` (any normal, Ocean, Magic Desk or EasyFlash cartridge you own): the cartridge starts instead of the BASIC banner; dropping a `.crt` on the window resets into it.
- [ ] ROM mode, `--reu 512` and a program that uses the REU (GEOS, a RAM disk driver, a demo): it finds 512 KB and works.
- [ ] JOY (Ctrl+J) cycles JOY- / JOY1 / JOY2 (the title says what it did); on JOY1 the cursor keys steer Pac-Man in Ms. Pac-Man (`LOAD"*",8,1` from the T64) and Space is fire; back on JOY- the cursor keys move the BASIC cursor again; switching while a key is held leaves nothing stuck.
- [ ] OPEN (and Ctrl+O) shows the system's file dialog (on Linux this needs zenity or kdialog, otherwise the title says so); picking a `.d64` in ROM mode resets and starts the game; cancelling does nothing; picking a `.crt` starts the cartridge; in the interpreter window a picked `.prg` or `.bas` loads and runs.
- [ ] A toolbar runs along the top (OPEN RESET WARP SAVE LOAD COPY PASTE SHOT FULL; ROM mode also PAUSE DISK- DISK+ TAPE REWIND); the text is readable at the default size and bigger in a large window; hovering a button shows its hint in the title; WARP and PAUSE light up while on; clicking works (RESET resets, SHOT writes a `.bmp`).
- [ ] The toolbar disappears in full screen (F11) and comes back; Ctrl+F12 hides and shows it; `--no-toolbar` starts without it; the picture keeps its proportions in a resized window, with the toolbar on or off.
- [ ] Shift + left drag over the text selects it (inverted cells, also backwards and over several lines); on release the title says "copied N characters" and pasting elsewhere gives the text. An ordinary click or a key drops the highlight.
- [ ] Middle click types the clipboard into the machine; COPY with nothing selected copies the whole screen.
- [ ] The mouse still works as a paddle or light pen (no Shift), and a click on the toolbar does not fire the joystick.

