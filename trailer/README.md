# NitroLite — 2-minute trailer

Remotion project, 1920 × 1080, 60 fps, 7200 frames (2:00). Everything on screen is a real asset: screen recordings of
the NitroLite build, the app's own icon and one NCS track. Nothing in the trailer imitates the app's UI.

## Asset checklist (`public/`)

```
public/
├── assets/
│   ├── logo.png           real app icon (256 px, extracted from NitroLite.Desktop/app.ico)
│   └── nitro-key.png      OPTIONAL — your own photo of the Nitro key; until it exists Act I shows the logo
├── audio/
│   └── ncs-track.mp3      Max Brhon – Cyberpunk [NCS Release], 3:35; the only sound in the trailer
└── clips/
    ├── 01-power-modes.mp4         cold launch, Quiet → Balanced → Performance → Turbo
    ├── 02-fan-curve.mp4           manual speed, curve templates, point drags, the floor zone
    ├── 03-rgb-effects.mp4         zone colours and all 8 effects
    ├── 04-game-profiles-osd.mp4   per-game profile, telemetry page, the OSD switched on
    ├── 05-battery-mux.mp4         charge limit, wear trend, the GPU/MUX selector
    ├── 06-themes-languages.mp4    6 themes, then TR DE ES HI ID PT RU (0.923 s each)
    └── 07-linux.mp4               OPTIONAL — if present, the last 5 s of Act VII play it
```

`src/config/assets.ts` maps every file, with each clip's trim and playback speed. Before preview and before every
render the composition checks which files exist and how long each clip is: a clip shorter than its act holds on its
last frame, a missing clip shows a plain "CLIP MISSING" plate, a missing `nitro-key.png` falls back to the logo.

## Commands

```bash
npm install
```

```bash
npx remotion studio src/index.ts
```

```bash
npx remotion render src/index.ts NitroLiteEpic out/nitrolite-2min-epic.mp4
```

Re-record the clips from the real app (builds nothing; run `dotnet build NitroLite.Desktop -c Release` first):

```bash
npm run record
```

`scripts/record-clips.ps1` drives the Release build with UI Automation and records each feature with
`ffmpeg -f gdigrab`; `scripts/encode-clips.ps1` encodes them to `public/clips` (H.264, CRF 16, yuv420p, 60 fps) and
prints an ffprobe check. The recorded instance runs on a throwaway copy of `%LOCALAPPDATA%\NitroLite`, so your
settings files are never written. Power mode, fan and keyboard lighting do change while it records; afterwards it puts
back the mode it found, automatic fan and the backlight off (pass `-RestoreMode Balanced` etc. to choose).

## Timing

| Frames | Time | Act | Footage |
|---|---|---|---|
| 0–840 | 0:00–0:14 | I · Ignition | logo, Nitro key, press on the track's first hit at 0:12.0 |
| 840–1800 | 0:14–0:30 | II · Power modes | 01 |
| 1800–2880 | 0:30–0:48 | III · Fan curves + safety floor | 02 |
| 2880–3960 | 0:48–1:06 | IV · 4-zone RGB, 8 effects | 03 |
| 3960–5040 | 1:06–1:24 | V · Per-game profiles + OSD | 04 |
| 5040–5880 | 1:24–1:38 | VI · Battery, MUX, specs | 05 |
| 5880–6600 | 1:38–1:50 | VII · Themes, languages, platforms | 06 (+07) |
| 6600–7200 | 1:50–2:00 | Finale | logo + signature |

The track starts at 23.0 s so its first impact lands on 0:12.0; impact flashes sit on the phrase starts nearest 0:46 and
1:18, and on the 1:50 cut. Music fades in over 1.5 s and out from 1:57.
