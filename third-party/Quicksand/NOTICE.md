# Quicksand

NitroLite draws its interface in Quicksand. The face is embedded in `NitroLite.exe` rather than looked up on the
machine, so the window is drawn in the same face on Windows, on Linux and on a machine that has installed nothing.

| | |
|---|---|
| Project | https://github.com/andrew-paglinawan/QuicksandFamily |
| Author | Andrew Paglinawan |
| Files | `Quicksand-Regular.ttf`, `Quicksand-SemiBold.ttf`, `Quicksand-Bold.ttf` |
| Licence | SIL Open Font License, Version 1.1 (reproduced below) |
| Reserved font name | "Quicksand" |

## Why the two licences do not conflict

The SIL Open Font License is not a copyleft licence over the software that uses the font. It governs the font files
themselves: they may be bundled, embedded and redistributed with any program, commercial or not, provided the licence
travels with them and the reserved font name is not reused for a modified version. NitroLite ships all three files
unmodified, so neither condition is strained, and NitroLite's own MIT licence is unaffected.

The three weights are the ones the type scale uses: Regular for body text, SemiBold for titles and labels, Bold for
the OSD. Static instances rather than the variable file, because Avalonia 11 exposes no variation axis and would draw
every weight at the file's default instance.

The licence text follows this notice.
