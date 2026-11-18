# Space Grotesk

NitroLite offers Space Grotesk as one of its interface typefaces — a retro-futuristic grotesque, the face the
sunset palette is drawn in. The face is embedded in `NitroLite.exe` rather than looked up on the machine, so the
window is drawn in the same face on Windows, on Linux and on a machine that has installed nothing.

| | |
|---|---|
| Project | https://github.com/floriankarsten/space-grotesk |
| Author | The Space Grotesk Project Authors |
| Files | `SpaceGrotesk-Regular.ttf`, `SpaceGrotesk-SemiBold.ttf`, `SpaceGrotesk-Bold.ttf` |
| Licence | SIL Open Font License, Version 1.1 (reproduced in `NitroLite.Desktop/Assets/Fonts/OFL.txt`) |
| Reserved font name | none |
| Copyright | Copyright 2020 The Space Grotesk Project Authors (https://github.com/floriankarsten/space-grotesk) |

## What was changed

The upstream file is a variable font. Avalonia 11 exposes no variation axis and would draw every weight at the
file's default instance, so the three static instances the type scale uses were generated from it with
`fontTools.varLib.instancer` at weights 400, 600 and 700. The glyphs were then subset to the ranges the eight
catalogue languages can put on screen — Latin, Latin Extended, Cyrillic, Devanagari, punctuation, currency,
arrows, mathematical operators and geometric shapes — which is what keeps the download small. No outline was
redrawn and no glyph was altered. The licence carries no Reserved Font Name, so the subset keeps the family name.

## Why the two licences do not conflict

The SIL Open Font License is not a copyleft licence over the software that uses the font. It governs the font files
themselves: they may be bundled, embedded and redistributed with any program, commercial or not, provided the licence
travels with them and the reserved font name is not reused for a modified version. NitroLite ships the files
unmodified apart from the subsetting described above, and the licence carries no reserved name, so neither condition
is strained. NitroLite's own MIT licence is unaffected.

## Script coverage

It carries no Cyrillic and no Devanagari, so Russian and Hindi fall through to the tail of the stack.

The three weights are the ones the type scale uses: Regular for body text, SemiBold for titles and labels, Bold for
the numbers.
