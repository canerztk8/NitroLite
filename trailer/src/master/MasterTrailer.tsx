import {getVideoMetadata} from '@remotion/media-utils';
import React from 'react';
import {AbsoluteFill, Audio, interpolate, OffthreadVideo, Sequence, staticFile, useCurrentFrame, useVideoConfig} from 'remotion';
import {AUDIO, AssetStatus, ClipKey} from '../config/assets';
import {COPY} from '../config/copy';
import {AssetImg, ClipPlayer} from '../components/Media';
import {FilmGrain, TelemetryGrid} from '../primitives';
import {C, DISPLAY, detent, INTER, linear, MONO, rgba, tabular, tween} from '../theme';

/**
 * 72 s master trailer, 1920 × 1080, 60 fps. Real assets only: the AI B-roll in public/broll, the screen recordings of
 * the NitroLite 1.2.7 exe in public/clips, the app icon and the NCS track.
 *
 * Music: Max Brhon – Cyberpunk at 110 BPM, started at 23.0 s so its first impact lands on 0:12. 12 s is exactly 22
 * beats, so 0:12 / 0:24 / 0:36 / 0:48 sit on beats; every other cut is snapped to the nearest beat with `snap`.
 */
export const M = {w: 1920, h: 1080, fps: 60, frames: 4320};
const BEAT = 60 / AUDIO.bpm;
const snapBeat = (t: number) => 12 + Math.round((t - 12) / BEAT) * BEAT;
const fr = (t: number) => Math.round(t * M.fps);

export const T = {
  key: snapBeat(7), // 6.909
  power: 12,
  fans: 24,
  fanUi: snapBeat(28), // 27.818
  rgb: 36,
  wave: snapBeat(43), // 43.091
  games: 48,
  battery: 12 + 83 * BEAT, // 57.273
  mux: 12 + 85 * BEAT, // 58.364, on "GPU switching"
  themes: 12 + 87 * BEAT, // 59.455, on "Fourteen themes"
  langs: 12 + 89 * BEAT, // 60.545, on "Eight languages"
  platforms: 12 + 92 * BEAT, // 62.182, the "1:02" impact, on "Windows and Linux"
  finale: 12 + 97 * BEAT, // 64.909, the card fades in over the lid on "NitroLite"
  end: 72,
};
const FLASHES = [T.power, T.fans, T.rgb, T.games, T.platforms].map(fr);

/**
 * Voiceover (Brian, ElevenLabs), one file per phrase from scripts/vo-master.py, and where each phrase starts.
 * Mode names land on the clicks in the recording; "Override" on the key press; "NitroLite" on the card.
 */
const VO: [number, number][] = [
  [0, 0.7], [1, 3.6], // built for full power… / waiting on bloatware?
  [2, 8.2], [3, 9.85], // one key / override
  [4, 13.6], [5, 16.5], [6, 19.4], [7, 22.3], [8, 23.05], // quiet / balanced / performance / turbo / one click
  [9, 28.05], [10, 30.6], // fan curve / safety floor
  [11, 37.0], [12, 43.35], // four zones, eight effects / your keyboard, your colors
  [13, 49.0], [14, 53.9], // every game… / live temps and clocks (the OSD appears near 54.2)
  [15, 57.35], [16, 59.6], [17, 60.85], [18, 62.35], // battery, GPU / themes / languages / Windows and Linux
  [19, 65.1], [20, 66.1], [21, 68.55], [22, 69.75], [23, 70.35], // NitroLite / 24 MB, no telemetry / no kernel driver / free / open source
];
const VO_LEN = [2.55, 2.32, 0.85, 0.75, 0.55, 0.78, 0.87, 0.66, 0.87, 2.39, 2.90, 2.06, 1.87, 3.12, 2.61, 2.14, 1.15, 0.91, 1.41, 0.91, 2.34, 1.08, 0.62, 1.36];
/** Music gain 1 -> 0.32 (-10 dB) under each phrase, 80 ms attack, 350 ms release. */
const duck = (frame: number) => {
  const t = frame / M.fps;
  let d = 0;
  VO.forEach(([i, at]) => {
    const a = at - 0.08;
    const b = at + VO_LEN[i];
    const v = t < a ? 0 : t < at ? (t - a) / 0.08 : t <= b ? 1 : t < b + 0.35 ? 1 - (t - b) / 0.35 : 0;
    d = Math.max(d, v);
  });
  return 1 - 0.68 * d;
};

// ------------------------------------------------------------------ B-roll
export const BROLL = {
  core: 'broll/01-core.mp4',
  key: 'broll/02-nitro-key.mp4',
  fans: 'broll/03-fans.mp4',
  waveLid: 'broll/04-rgb-wave05-lid-close.mp4',
} as const;
export type BrollKey = keyof typeof BROLL;
export type BrollStatus = Record<BrollKey, number | null>;

export async function probeBroll(): Promise<BrollStatus> {
  const e = await Promise.all(
    (Object.keys(BROLL) as BrollKey[]).map(async (k) => {
      const d = await Promise.race([
        getVideoMetadata(staticFile(BROLL[k]))
          .then((m) => m.durationInSeconds)
          .catch(() => null),
        new Promise<null>((r) => setTimeout(() => r(null), 15000)),
      ]);
      return [k, d] as const;
    }),
  );
  return Object.fromEntries(e) as BrollStatus;
}

/** A muted B-roll segment [s0, s1] (seconds in the file) stretched over its sequence, cropped 6 % for edge artifacts. */
const Broll: React.FC<{k: BrollKey; src: [number, number]; slot: number; status: BrollStatus; dim?: number; crop?: {scale: number; origin: string}}> = ({
  k,
  src,
  slot,
  status,
  dim = 0,
  crop = {scale: 1.06, origin: '50% 50%'},
}) => {
  const {fps} = useVideoConfig();
  if (status[k] === null)
    return (
      <AbsoluteFill style={{background: '#0B0D12', alignItems: 'center', justifyContent: 'center', fontFamily: MONO, color: C.muted, fontSize: 22}}>
        B-ROLL MISSING · public/{BROLL[k]}
      </AbsoluteFill>
    );
  return (
    <AbsoluteFill style={{overflow: 'hidden'}}>
      <OffthreadVideo
        src={staticFile(BROLL[k])}
        muted
        trimBefore={Math.round(src[0] * fps)}
        playbackRate={(src[1] - src[0]) / (slot / fps)}
        style={{width: '100%', height: '100%', objectFit: 'cover', transform: `scale(${crop.scale})`, transformOrigin: crop.origin}}
      />
      {dim > 0 && <AbsoluteFill style={{background: `rgba(6,7,10,${dim})`}} />}
    </AbsoluteFill>
  );
};

// ------------------------------------------------------------------ real UI viewport
type Focus = {x: number; y: number}; // 0..1 of the recording
const STAGE = {x: 240, y: 150, w: 1440, h: 810};

/** A screen recording floating on obsidian: bezel, crimson glow, camera easing 1.00 → 1.12 towards `to`. */
const UiStage: React.FC<{clip: ClipKey; src: [number, number]; slot: number; status: AssetStatus; from: Focus; to: Focus; zoom?: [number, number]}> = ({
  clip,
  src,
  slot,
  status,
  from,
  to,
  zoom = [1, 1.12],
}) => {
  const f = useCurrentFrame();
  const {fps} = useVideoConfig();
  const u = interpolate(f, [0, slot], [0, 1], {easing: detent, extrapolateRight: 'clamp'});
  const z = zoom[0] + (zoom[1] - zoom[0]) * u;
  const clampF = (v: number) => Math.min(1 - 0.5 / z, Math.max(0.5 / z, v));
  const fx = clampF(from.x + (to.x - from.x) * u);
  const fy = clampF(from.y + (to.y - from.y) * u);
  const settle = tween(f, 0, 16);
  const {x, y, w, h} = STAGE;
  return (
    <AbsoluteFill>
      <div
        style={{
          position: 'absolute',
          left: x,
          top: y + Math.sin(f / 90) * 3,
          width: w,
          height: h,
          overflow: 'hidden',
          background: '#0B0D12',
          border: '1px solid rgba(255,255,255,0.12)',
          borderRadius: 6,
          boxShadow: `0 0 120px ${rgba(C.crimson, 0.22)}, 0 50px 120px rgba(0,0,0,0.8), inset 0 1px 0 rgba(255,255,255,0.08)`,
          transform: `scale(${1.03 - 0.03 * settle})`,
        }}
      >
        <div
          style={{
            position: 'absolute',
            left: 0,
            top: 0,
            width: w,
            height: h,
            transformOrigin: '0 0',
            transform: `translate(${w / 2}px, ${h / 2}px) scale(${z}) translate(${-fx * w}px, ${-fy * h}px)`,
          }}
        >
          <ClipPlayer clip={clip} slot={slot} status={status} trimStart={src[0]} rate={(src[1] - src[0]) / (slot / fps)} />
        </div>
        <div style={{position: 'absolute', inset: 0, background: 'linear-gradient(180deg, rgba(255,255,255,0.05), transparent 16%, transparent 86%, rgba(0,0,0,0.3))'}} />
      </div>
      <div style={{position: 'absolute', left: x + w - 260, width: 260, top: y + h + 14, textAlign: 'right', fontFamily: MONO, fontSize: 13, letterSpacing: '0.2em', color: C.cyan, opacity: 0.8, ...tabular}}>
        {`LIVE CAPTURE · ${z.toFixed(2)}×`}
      </div>
    </AbsoluteFill>
  );
};

// ------------------------------------------------------------------ typography
const Header: React.FC<{text: string}> = ({text}) => {
  const f = useCurrentFrame();
  const p = tween(f, 2, 30);
  const [n, title] = text.split(' // ');
  return (
    <div style={{position: 'absolute', left: 64, top: 40, opacity: tween(f, 0, 8, 0, 1, linear), transform: `translateX(${(1 - p) * -36}px)`}}>
      <div style={{fontFamily: DISPLAY, fontWeight: 800, fontSize: 52, lineHeight: 1, letterSpacing: '0.12em', color: C.titanium, textTransform: 'uppercase', textShadow: '0 3px 18px rgba(0,0,0,0.9)', whiteSpace: 'nowrap'}}>
        <span style={{color: C.crimson}}>{n}</span>
        <span style={{color: C.muted}}>{'  //  '}</span>
        {title}
      </div>
    </div>
  );
};

const Badge: React.FC<{children: React.ReactNode; color: string; at: number}> = ({children, color, at}) => {
  const f = useCurrentFrame();
  const p = tween(f, at, at + 22);
  return (
    <div
      style={{
        opacity: tween(f, at, at + 6, 0, 1, linear),
        transform: `translateX(${(1 - p) * -40}px)`,
        padding: '10px 18px',
        borderRadius: 999,
        border: `1px solid ${color}`,
        background: 'rgba(10,12,18,0.85)',
        fontFamily: MONO,
        fontWeight: 500,
        fontSize: 18,
        letterSpacing: '0.12em',
        color: C.titanium,
        whiteSpace: 'nowrap',
        display: 'flex',
        alignItems: 'center',
        gap: 12,
        ...tabular,
      }}
    >
      <span style={{width: 7, height: 7, borderRadius: 4, background: color}} />
      {children}
    </div>
  );
};

const Badges: React.FC<{items: string[]; start?: number}> = ({items, start = 14}) => (
  <div style={{position: 'absolute', left: 64, right: 64, bottom: 34, display: 'flex', gap: 14, flexWrap: 'wrap'}}>
    {items.map((t, i) => (
      <Badge key={t} color={i % 2 ? C.cyan : C.crimson} at={start + i * 9}>
        {t}
      </Badge>
    ))}
  </div>
);

/** Act header + badges for a whole act, independent of the cuts inside it. */
const Hud: React.FC<{header: string; badges: string[]}> = ({header, badges}) => (
  <AbsoluteFill style={{pointerEvents: 'none'}}>
    <AbsoluteFill style={{background: 'linear-gradient(180deg, rgba(6,7,10,0.75), transparent 16%, transparent 82%, rgba(6,7,10,0.8))'}} />
    <Header text={header} />
    <Badges items={badges} />
  </AbsoluteFill>
);

// ------------------------------------------------------------------ Act I / II / finale pieces
const Kinetic: React.FC<{line: string; sub: string; out: number}> = ({line, sub, out}) => {
  const f = useCurrentFrame();
  const fadeOut = 1 - tween(f, out - 18, out, 0, 1, linear);
  // two rows: "YOUR HARDWARE WAS BUILT" / "FOR FULL POWER."
  const words = line.split(' ');
  const rows = [words.slice(0, 4), words.slice(4)];
  let n = 0;
  return (
    <AbsoluteFill style={{alignItems: 'center', justifyContent: 'center', opacity: fadeOut}}>
      {rows.map((row, r) => (
        <div key={r} style={{display: 'flex', gap: 34, justifyContent: 'center', marginTop: r ? 6 : 0}}>
          {row.map((w) => {
            const a = n++ * 7;
            const p = tween(f, a, a + 24);
            const hot = w.startsWith('FULL') || w.startsWith('POWER');
            return (
              <span
                key={w}
                style={{
                  fontFamily: DISPLAY,
                  fontWeight: 800,
                  fontSize: r ? 118 : 84,
                  lineHeight: 1.05,
                  letterSpacing: '0.12em',
                  color: hot ? C.crimson : C.titanium,
                  opacity: tween(f, a, a + 8, 0, 1, linear),
                  transform: `translateY(${(1 - p) * 26}px)`,
                  filter: `blur(${(1 - p) * 6}px)`,
                  textShadow: '0 4px 30px rgba(0,0,0,0.95)',
                  whiteSpace: 'nowrap',
                }}
              >
                {w}
              </span>
            );
          })}
        </div>
      ))}
      <div style={{marginTop: 34, fontFamily: MONO, fontSize: 24, letterSpacing: '0.3em', color: C.titanium, opacity: 0.75 * tween(f, 70, 90, 0, 1, linear), textShadow: '0 2px 16px #000'}}>
        {sub}
      </div>
    </AbsoluteFill>
  );
};

const Stamp: React.FC<{text: string; out: number}> = ({text, out}) => {
  const f = useCurrentFrame();
  const p = tween(f, 0, 14);
  const blink = f < 30 ? (Math.floor(f / 4) % 2 ? 0.4 : 1) : 1;
  return (
    <div style={{position: 'absolute', left: 64, bottom: 90, opacity: blink * (1 - tween(f, out - 10, out, 0, 1, linear)), transform: `scale(${1.25 - 0.25 * p})`, transformOrigin: 'left center'}}>
      <div style={{fontFamily: MONO, fontSize: 14, letterSpacing: '0.3em', color: C.crimson, marginBottom: 10}}>SYS · NITRO KEY</div>
      <div style={{padding: '14px 26px', border: `1px solid ${C.crimson}`, background: 'rgba(10,12,18,0.85)', fontFamily: MONO, fontWeight: 700, fontSize: 34, letterSpacing: '0.24em', color: C.titanium}}>
        {text}
      </div>
    </div>
  );
};

const FinalCard: React.FC<{status: AssetStatus}> = ({status}) => {
  const f = useCurrentFrame();
  const show = (a: number) => ({opacity: tween(f, a, a + 14, 0, 1, linear), transform: `translateY(${(1 - tween(f, a, a + 26)) * 22}px)`});
  return (
    <AbsoluteFill style={{alignItems: 'center'}}>
      <AbsoluteFill style={{background: C.obsidian, opacity: tween(f, 0, 110, 0.55, 1, linear)}} />
      <TelemetryGrid opacity={0.6 * tween(f, 40, 110, 0, 1, linear)} />
      <AbsoluteFill style={{background: `radial-gradient(circle at 50% 34%, ${rgba(C.crimson, 0.2)}, transparent 38%)`}} />
      <div style={{position: 'absolute', top: 200, width: 210, height: 210, borderRadius: 42, overflow: 'hidden', boxShadow: `0 0 140px ${rgba(C.crimson, 0.4)}`, ...show(4)}}>
        <AssetImg img="logo" status={status} style={{width: '100%', height: '100%'}} />
      </div>
      <div style={{position: 'absolute', top: 450, fontFamily: DISPLAY, fontWeight: 800, fontSize: 150, lineHeight: 1, letterSpacing: '0.12em', paddingLeft: '0.12em', color: C.titanium, textShadow: '0 4px 30px rgba(0,0,0,0.9)', ...show(10)}}>
        NITROLITE
      </div>
      <div style={{position: 'absolute', top: 640, fontFamily: MONO, fontSize: 26, letterSpacing: '0.18em', color: C.titanium, ...tabular, ...show(70)}}>
        {COPY_MASTER.spec1}
      </div>
      <div style={{position: 'absolute', top: 696, fontFamily: MONO, fontSize: 26, letterSpacing: '0.18em', color: C.muted, ...tabular, ...show(290)}}>
        {COPY_MASTER.spec2}
      </div>
      <div style={{position: 'absolute', top: 790, fontFamily: MONO, fontSize: 30, color: C.titanium, padding: '14px 26px', border: '1px solid rgba(245,247,250,0.2)', borderLeft: `3px solid ${C.crimson}`, ...show(330)}}>
        {COPY.url}
      </div>
    </AbsoluteFill>
  );
};

/**
 * On-screen text. Checked against the README of 1.2.7; where the brief's wording was not true it is corrected:
 * the exe is 24 MB (25.3 MB on disk, 24.1 MiB), the suite has 2652 tests, there are fourteen themes, fan and lighting
 * go through the Acer service or the firmware's WMI class (sysfs on Linux), and nothing measured backs a latency claim.
 */
export const COPY_MASTER = {
  act1: 'YOUR HARDWARE WAS BUILT FOR FULL POWER.',
  act1Sub: 'STOP LETTING BLOATWARE HOLD IT BACK.',
  stamp: 'OVERRIDE INITIATED',
  power: {header: '01 // INSTANT POWER MODES', badges: ['QUIET • BALANCED • PERFORMANCE • TURBO', 'ACER WMI ON WINDOWS • SYSFS ON LINUX']},
  fan: {header: '02 // CUSTOM FAN CURVES', badges: ['POINT-BY-POINT FAN CURVE', 'MANDATORY THERMAL SAFETY FLOOR (85°C / 90°C / 95°C)']},
  rgb: {header: '03 // 4-ZONE KEYBOARD RGB', badges: ['8 LIGHTING EFFECTS', 'PER-ZONE COLOUR • BRIGHTNESS • SPEED']},
  games: {header: '04 // PER-GAME PROFILES & LIVE OSD', badges: ['AUTO-SWITCH ON GAME LAUNCH', 'REAL-TIME CPU / GPU TEMPS, CLOCKS & WATTAGE']},
  rest: {header: '05 // BUILT FOR WINDOWS & LINUX', badges: ['80% BATTERY LIMITER & MUX', '14 THEMES • 8 LANGUAGES', '31 ACER NITRO & PREDATOR MODELS']},
  spec1: '24 MB EXE  •  NO TELEMETRY  •  NO KERNEL DRIVER',
  spec2: '2,652 AUTOMATED TESTS  •  FREE & OPEN SOURCE',
};

// ------------------------------------------------------------------ composition
export type MasterProps = {assets: AssetStatus | null; broll: BrollStatus | null};
const EMPTY: AssetStatus = {
  clips: {powerModes: null, fanCurve: null, rgb: null, gameOsd: null, batteryMux: null, themes: null, linux: null},
  images: {logo: false, nitroKey: false},
  audio: false,
};
const NO_BROLL: BrollStatus = {core: null, key: null, fans: null, waveLid: null};

/** Sequence from a to b (seconds). */
const Seg: React.FC<{a: number; b: number; name: string; children: React.ReactNode}> = ({a, b, name, children}) => (
  <Sequence from={fr(a)} durationInFrames={fr(b) - fr(a)} name={name}>
    {children}
  </Sequence>
);
const slot = (a: number, b: number) => fr(b) - fr(a);

export const MasterTrailer: React.FC<MasterProps> = ({assets, broll}) => {
  const f = useCurrentFrame();
  const st = assets ?? EMPTY;
  const br = broll ?? NO_BROLL;
  // 2-frame micro punch on each impact
  const punch = FLASHES.reduce((k, at) => (f - at >= 0 && f - at < 2 ? 1 - (f - at) / 2 : k), 0);
  const flash = FLASHES.reduce((k, at) => (f - at >= 0 && f - at < 4 ? Math.max(k, 0.85 * (1 - (f - at) / 4)) : k), 0);
  const flashWhite = FLASHES.some((at) => f - at >= 0 && f - at < 2);
  return (
    <AbsoluteFill style={{background: C.obsidian}}>
      <AbsoluteFill style={{transform: `scale(${1 + 0.04 * punch})`}}>
        <TelemetryGrid />

        {/* I · Dormant power */}
        <Seg a={0} b={T.key} name="I · Dormant power">
          <Broll k="core" src={[2.4, 2.4 + T.key]} slot={slot(0, T.key)} status={br} dim={0.25} />
          <AbsoluteFill style={{background: 'radial-gradient(ellipse 70% 60% at 50% 50%, transparent 30%, rgba(6,7,10,0.75))'}} />
          <LogoMark status={st} />
          <Sequence from={fr(1.5)} durationInFrames={fr(6.5) - fr(1.5)} name="kinetic">
            <Kinetic line={COPY_MASTER.act1} sub={COPY_MASTER.act1Sub} out={fr(6.5) - fr(1.5)} />
          </Sequence>
        </Seg>

        {/* II · Ignition: the press (4.0 s in the file) lands near 0:10, its ring grows into the 0:12 cut */}
        <Seg a={T.key} b={T.power} name="II · Ignition">
          <Broll k="key" src={[0.9, 0.9 + (T.power - T.key)]} slot={slot(T.key, T.power)} status={br} />
          <Sequence from={fr(8.5) - fr(T.key)} durationInFrames={fr(11.8) - fr(8.5)}>
            <Stamp text={COPY_MASTER.stamp} out={fr(11.8) - fr(8.5)} />
          </Sequence>
        </Seg>

        {/* III · Power modes */}
        <Seg a={T.power} b={T.fans} name="III · Power modes">
          <UiStage clip="powerModes" src={SRC.power} slot={slot(T.power, T.fans)} status={st} from={{x: 0.3, y: 0.27}} to={{x: 0.72, y: 0.27}} />
          <Hud {...COPY_MASTER.power} />
        </Seg>

        {/* IV · Fans: turbines spool up, hard cut into the curve */}
        <Seg a={T.fans} b={T.rgb} name="IV · Fan curves">
          <Sequence durationInFrames={slot(T.fans, T.fanUi)}>
            <Broll k="fans" src={[4.4, 4.4 + (T.fanUi - T.fans)]} slot={slot(T.fans, T.fanUi)} status={br} />
          </Sequence>
          <Sequence from={slot(T.fans, T.fanUi)}>
            <UiStage clip="fanCurve" src={SRC.fan} slot={slot(T.fanUi, T.rgb)} status={st} from={{x: 0.5, y: 0.6}} to={{x: 0.62, y: 0.68}} />
          </Sequence>
          <Hud {...COPY_MASTER.fan} />
        </Seg>

        {/* V · RGB: UI colour shift, match cut to the keyboard wave (first 4.6 s of the file, before any brand mark) */}
        <Seg a={T.rgb} b={T.games} name="V · RGB">
          <Sequence durationInFrames={slot(T.rgb, T.wave)}>
            <UiStage clip="rgb" src={SRC.rgb} slot={slot(T.rgb, T.wave)} status={st} from={{x: 0.4, y: 0.26}} to={{x: 0.6, y: 0.3}} />
          </Sequence>
          <Sequence from={slot(T.rgb, T.wave)}>
            <Broll k="waveLid" src={[0.2, 4.6]} slot={slot(T.wave, T.games)} status={br} crop={LID_CROP} />
          </Sequence>
          <Hud {...COPY_MASTER.rgb} />
        </Seg>

        {/* VI · Games + OSD */}
        <Seg a={T.games} b={T.battery} name="VI · Games + OSD">
          <UiStage clip="gameOsd" src={SRC.games} slot={slot(T.games, T.battery)} status={st} from={{x: 0.4, y: 0.7}} to={{x: 0.2, y: 0.1}} zoom={[1, 1.12]} />
          <Hud {...COPY_MASTER.games} />
        </Seg>

        {/* VII · Battery, MUX, themes and languages in fast cuts */}
        <Seg a={T.battery} b={T.finale} name="VII · Battery, themes, languages">
          <Sequence durationInFrames={slot(T.battery, T.mux)}>
            <UiStage clip="batteryMux" src={SRC.battery} slot={slot(T.battery, T.mux)} status={st} from={FOCUS.battery[0]} to={FOCUS.battery[1]} />
          </Sequence>
          <Sequence from={slot(T.battery, T.mux)} durationInFrames={slot(T.mux, T.themes)}>
            <UiStage clip="batteryMux" src={SRC.mux} slot={slot(T.mux, T.themes)} status={st} from={FOCUS.mux[0]} to={FOCUS.mux[1]} />
          </Sequence>
          <Sequence from={slot(T.battery, T.themes)} durationInFrames={slot(T.themes, T.langs)}>
            <UiStage clip="themes" src={SRC.themes} slot={slot(T.themes, T.langs)} status={st} from={{x: 0.5, y: 0.5}} to={{x: 0.45, y: 0.45}} zoom={[1, 1.04]} />
          </Sequence>
          <Sequence from={slot(T.battery, T.langs)} durationInFrames={slot(T.langs, T.platforms)}>
            <UiStage clip="themes" src={SRC.langs} slot={slot(T.langs, T.platforms)} status={st} from={{x: 0.3, y: 0.3}} to={{x: 0.3, y: 0.3}} zoom={[1, 1.06]} />
          </Sequence>
          <Sequence from={slot(T.battery, T.platforms)}>
            <Platforms slot={slot(T.platforms, T.finale)} status={br} />
          </Sequence>
          <Hud {...COPY_MASTER.rest} />
        </Seg>

        {/* Finale: lid closing (before the lid emblem turns to camera), fade to obsidian, the seal */}
        <Seg a={T.finale} b={T.end} name="Finale">
          <Sequence durationInFrames={slot(T.finale, T.finale + 2.4)}>
            <Broll k="waveLid" src={[11.0, 13.4]} slot={slot(T.finale, T.finale + 2.4)} status={br} crop={LID_CROP} />
          </Sequence>
          <FinalCard status={st} />
        </Seg>

        <AbsoluteFill style={{pointerEvents: 'none', background: 'radial-gradient(ellipse 80% 70% at 50% 50%, transparent 62%, rgba(0,0,0,0.55))'}} />
      </AbsoluteFill>
      {flash > 0 && <AbsoluteFill style={{background: flashWhite ? rgba(C.titanium, flash) : rgba(C.crimson, flash)}} />}
      <FilmGrain opacity={0.03} />
      {VO.map(([i, at]) => (
        <Sequence key={i} from={Math.round((at - 0.03 / 1.05) * M.fps)} durationInFrames={Math.ceil((VO_LEN[i] + 0.3) * M.fps)} name={`VO ${i}`}>
          <Audio src={staticFile(`audio/vo/${String(i).padStart(2, '0')}.wav`)} />
        </Sequence>
      ))}
      {st.audio && (
        <Audio
          src={staticFile(AUDIO.src)}
          trimBefore={Math.round(AUDIO.startAt * M.fps)}
          // the master of this track is hot (-8 LUFS, clipping); 0.5 brings it near -14 LUFS with headroom
          volume={(x) => 0.5 * duck(x) * interpolate(x, [0, 60, M.frames - 180, M.frames], [0, 1, 1, 0], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'})}
        />
      )}
    </AbsoluteFill>
  );
};

/** "Windows & Linux": a typographic beat over the core B-roll on the 1:02 impact. */
const Platforms: React.FC<{slot: number; status: BrollStatus}> = ({slot, status}) => {
  const f = useCurrentFrame();
  const p = tween(f, 0, 26);
  return (
    <AbsoluteFill>
      <Broll k="core" src={[6.5, 6.5 + slot / M.fps]} slot={slot} status={status} dim={0.45} />
      <AbsoluteFill style={{alignItems: 'center', justifyContent: 'center'}}>
        <div style={{fontFamily: DISPLAY, fontWeight: 800, fontSize: 130, letterSpacing: `${0.3 - 0.18 * p}em`, color: C.titanium, textShadow: '0 4px 40px rgba(0,0,0,0.95)', opacity: tween(f, 0, 8, 0, 1, linear)}}>
          WINDOWS <span style={{color: C.crimson}}>&amp;</span> LINUX
        </div>
        <div style={{marginTop: 22, fontFamily: MONO, fontSize: 24, letterSpacing: '0.3em', color: C.titanium, opacity: 0.8 * tween(f, 18, 30, 0, 1, linear)}}>
          31 ACER NITRO &amp; PREDATOR MODELS
        </div>
      </AbsoluteFill>
    </AbsoluteFill>
  );
};

/** Logo watermark over the core shot. */
const LogoMark: React.FC<{status: AssetStatus}> = ({status}) => {
  const f = useCurrentFrame();
  const o = tween(f, 20, 120, 0, 0.55, linear) * (1 - tween(f, fr(6.3), fr(6.9), 0, 1, linear));
  return (
    <div style={{position: 'absolute', right: 64, top: 52, display: 'flex', alignItems: 'center', gap: 16, opacity: o}}>
      <div style={{width: 54, height: 54, borderRadius: 12, overflow: 'hidden'}}>
        <AssetImg img="logo" status={status} style={{width: '100%', height: '100%'}} />
      </div>
      <div style={{fontFamily: INTER, fontWeight: 600, fontSize: 20, letterSpacing: '0.3em', color: C.titanium}}>NITROLITE</div>
    </div>
  );
};

/** Seconds inside each recording used by each cut (filled in after looking at the 1.2.7 captures). */
const SRC: Record<'power' | 'fan' | 'rgb' | 'games' | 'battery' | 'mux' | 'themes' | 'langs', [number, number]> = {
  power: [3.0, 17.5], // Quiet ≈5 s, Balanced ≈8.5, Performance ≈12, Turbo ≈15.5
  fan: [6.5, 20.0],
  rgb: [6.0, 22.0],
  games: [7.0, 25.7], // profile combos, then the telemetry page and the OSD from ≈19.5 s
  battery: [3.5, 5.6], // charge limit and wear on the Battery page
  mux: [15.0, 17.2], // back on Operating Modes, hovering the GPU / MUX selector
  themes: [0.1, 5.6], // the six theme captures
  langs: [5.6, 12.1], // the seven language captures
};
/** The generated laptop in 04 carries another maker's name on its bezel, in the top fifth of the frame: anchor the
 * crop to the bottom edge and enlarge enough that it never enters the picture. */
const LID_CROP = {scale: 1.32, origin: '50% 100%'};
const FOCUS: Record<'battery' | 'mux', [Focus, Focus]> = {
  battery: [{x: 0.35, y: 0.22}, {x: 0.42, y: 0.32}],
  mux: [{x: 0.55, y: 0.52}, {x: 0.66, y: 0.6}],
};
