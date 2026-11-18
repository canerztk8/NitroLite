# Rajdhani

NitroLite offers Rajdhani as one of its interface typefaces — condensed HUD. The face is embedded in `NitroLite.exe`
rather than looked up on the machine, so the window is drawn in the same face on Windows, on Linux and on a machine
that has installed nothing.

| | |
|---|---|
| Project | https://github.com/itfoundry/rajdhani |
| Author | Indian Type Foundry |
| Files | `Rajdhani-Regular.ttf`, `Rajdhani-SemiBold.ttf`, `Rajdhani-Bold.ttf` |
| Licence | SIL Open Font License, Version 1.1 (reproduced in `NitroLite.Desktop/Assets/Fonts/OFL.txt`) |
| Reserved font name | none |
| Copyright | Copyright (c) 2014, Indian Type Foundry (info@indiantypefoundry.com). |

## What was changed

The upstream files are already static, so no instance was generated. The glyphs were subset to the ranges the
eight catalogue languages can put on screen — Latin, Latin Extended, Cyrillic, Devanagari, punctuation, currency,
arrows, mathematical operators and geometric shapes — which is what keeps the download small. No outline was
redrawn and no glyph was altered. The licence carries no Reserved Font Name, so the subset keeps the family name.

## Why the two licences do not conflict

The SIL Open Font License is not a copyleft licence over the software that uses the font. It governs the font files
themselves: they may be bundled, embedded and redistributed with any program, commercial or not, provided the licence
travels with them and the reserved font name is not reused for a modified version. NitroLite ships the files
unmodified apart from the subsetting described above, and the licence carries no reserved name, so neither condition
is strained. NitroLite's own MIT licence is unaffected.

## Script coverage

It carries Devanagari, so a reader of that language gets the window in this face rather than the
fallback at the end of the stack.

The three weights are the ones the type scale uses: Regular for body text, SemiBold for titles and labels, Bold for
the numbers.
