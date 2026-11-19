/**
 * Every word on screen. Checked against the NitroLite 1.2.6 README and source; where the brief and the project
 * disagree, the project wins and the note says why.
 *
 *  - Footprint: README measures ~73 MB of RAM in the tray (the brief said ~74 / ~70 MB).
 *  - Thermal floor 85 °C ≥ 60 %, 90 °C ≥ 80 %, 95 °C = 100 %; manual speed is 30–100 % (so "0 % manual" can't happen).
 *  - Effects: the eight names are the ones the app's own effect list shows on this machine.
 *  - Per-game profiles switch power mode, CPU boost and fan (README), not RGB, and restore on exit.
 *  - OSD: CPU/GPU temperature, power, clock, usage/VRAM, RAM and fan (README). No FPS counter.
 *  - Anti-cheat: the README says no kernel driver / no WinRing0 is loaded, and explicitly that this is NOT a guarantee
 *    an anti-cheat will accept it, so the trailer does not claim "anti-cheat safe".
 *  - "0.4s INSTANT LAUNCH" is not documented anywhere in the project; kept because the brief asks for it.
 */
export const COPY = {
  url: 'github.com/canerztk8/NitroLite',
  version: 'v1.2.6',
  act1Title: 'YOUR HARDWARE WAS BUILT FOR FULL POWER.',
  act1Key: 'TAKE BACK CONTROL.',
  act1Stamp: ['0.4s INSTANT LAUNCH', '~73 MB', 'ZERO BLOAT'],

  power: {
    act: 'ACT II',
    title: 'INSTANT POWER MODES',
    callouts: ['ONE-CLICK POWER PROFILES', 'DIRECT WMI & SYSFS FIRMWARE CONTROL', "WORKS WITHOUT ACER'S SOFTWARE"],
    modes: ['QUIET', 'BALANCED', 'PERFORMANCE', 'TURBO'],
  },
  fan: {
    act: 'ACT III',
    title: 'CUSTOM FAN CURVES',
    callouts: ['MANUAL SPEED 30–100 %', 'POINT-BY-POINT FAN CURVES'],
    badge: 'MANDATORY THERMAL SAFETY FLOOR',
    floor: ['85°C → ≥60 %', '90°C → ≥80 %', '95°C → 100 %'],
    note: 'Even at the lowest manual fan speed, the thermal floor takes over.',
  },
  rgb: {
    act: 'ACT IV',
    title: '4-ZONE RGB',
    callouts: ['4-ZONE RGB CONTROL', '8 HARDWARE EFFECTS', 'APPLIED INSTANTLY'],
    effects: ['STATIC', 'WAVE', 'BREATHING', 'NEON', 'SHIFTING', 'ZOOM', 'METEOR', 'TWINKLING'],
  },
  games: {
    act: 'ACT V',
    title: 'PER-GAME AUTOMATION + LIVE OSD',
    callouts: ['AUTOMATIC PER-GAME PROFILES', 'MODE · BOOST · FAN — RESTORED ON EXIT', 'LIVE IN-GAME HARDWARE OSD'],
  },
  battery: {
    act: 'ACT VI',
    title: 'BATTERY · MUX · STEALTH',
    callouts: ['80 % CHARGE LIMIT', 'BATTERY WEAR HISTORY', 'GPU / MUX MODE'],
    specs: [
      {big: '~73 MB', small: 'RAM IN THE TRAY · NO ELECTRON'},
      {big: 'ZERO', small: 'TELEMETRY'},
      {big: 'NO KERNEL DRIVER', small: 'LOADS NO WINRING0'},
    ],
  },
  themes: {
    act: 'ACT VII',
    title: '6 THEMES · 8 LANGUAGES',
    callouts: ['WINDOWS 11 + LINUX', '31 ACER NITRO & PREDATOR MODELS'],
    // in the order of clips/06-themes-languages.mp4, 0.923 s each
    shots: [
      'THEME · DARK',
      'THEME · MIDNIGHT OLED',
      'THEME · SLATE STEEL',
      'THEME · AMBER CONSOLE',
      'THEME · NORD FROST',
      'THEME · WARM CREAM',
      'LANG · TR',
      'LANG · DE',
      'LANG · ES',
      'LANG · HI',
      'LANG · ID',
      'LANG · PT',
      'LANG · RU',
    ],
    shotSeconds: 0.923,
    langs: ['EN', 'TR', 'DE', 'ES', 'HI', 'ID', 'PT', 'RU'],
  },
  finale: {
    title: 'NITROLITE',
    lines: ['31 ACER NITRO & PREDATOR MODELS  •  WINDOWS & LINUX', 'FREE & OPEN SOURCE  •  MIT LICENSE'],
  },
} as const;
