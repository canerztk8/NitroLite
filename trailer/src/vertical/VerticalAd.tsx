import React from 'react';
import {AbsoluteFill, interpolate, Sequence, useCurrentFrame, useVideoConfig} from 'remotion';
import {AssetStatus, ClipKey} from '../config/assets';
import {COPY} from '../config/copy';
import {AssetImg, ClipPlayer} from '../components/Media';
import {FilmGrain, HudTag, Label, TelemetryGrid} from '../primitives';
import {C, DISPLAY, detent, linear, MONO, rgba, tween} from '../theme';

/**
 * 29.7 s vertical ad (1080 × 1920) for Shorts / TikTok / Reels. Silent: voiceover and music are added afterwards.
 * Text and footage stay inside the platform safe area (clear of the top bar, the right-hand buttons and the bottom
 * caption block); set `showSafeZones` in the Studio to see it.
 */
/** Scene cuts in seconds, placed on the voiceover lines and snapped to kicks in the track (see scripts/mix-vertical.py). */
export const CUT = {reveal: 4.838, shots: [9.603, 12.805, 16.637, 20.099, 23.551], end: 25.876, total: 29.69, hookSub: 3.141};
export const V = {w: 1080, h: 1920, fps: 60, frames: Math.round(CUT.total * 60)};

type Shot = {
  from: number; // seconds
  to: number;
  kicker: string;
  title: string[];
  clip?: ClipKey;
  src?: [number, number]; // seconds inside the recording
  cam?: {t: number; x: number; y: number; z: number}[]; // t is 0..1 across the shot
  chip?: string;
};

export const SHOTS: Shot[] = [
  {from: CUT.shots[0], to: CUT.shots[1], kicker: '01 · POWER', title: ['ONE-CLICK', 'POWER MODES'], clip: 'powerModes', src: [4.0, 11.9],
    cam: [{t: 0, x: 0.27, y: 0.3, z: 1}, {t: 0.3, x: 0.47, y: 0.3, z: 1}, {t: 0.6, x: 0.67, y: 0.3, z: 1}, {t: 0.9, x: 0.86, y: 0.3, z: 1}],
    chip: 'QUIET → BALANCED → PERFORMANCE → TURBO'},
  {from: CUT.shots[1], to: CUT.shots[2], kicker: '02 · COOLING', title: ['YOUR OWN', 'FAN CURVE'], clip: 'fanCurve', src: [6.5, 17],
    cam: [{t: 0, x: 0.45, y: 0.68, z: 1.15}, {t: 0.65, x: 0.6, y: 0.68, z: 1.15}, {t: 1, x: 0.8, y: 0.68, z: 1.3}],
    chip: 'THERMAL FLOOR 85 / 90 / 95 °C'},
  {from: CUT.shots[2], to: CUT.shots[3], kicker: '03 · LIGHTING', title: ['4-ZONE RGB', '8 EFFECTS'], clip: 'rgb', src: [8, 22],
    cam: [{t: 0, x: 0.42, y: 0.24, z: 1.1}, {t: 1, x: 0.7, y: 0.24, z: 1.1}],
    chip: 'STATIC · WAVE · NEON · METEOR …'},
  {from: CUT.shots[3], to: CUT.shots[4], kicker: '04 · IN GAME', title: ['LIVE', 'OSD OVERLAY'], clip: 'gameOsd', src: [21.5, 25.7],
    cam: [{t: 0, x: 0.2, y: 0.2, z: 1.3}, {t: 1, x: 0.15, y: 0.08, z: 1.7}],
    chip: 'CPU · GPU · TEMPS · CLOCKS · POWER'},
  {from: CUT.shots[4], to: CUT.end, kicker: '05 · YOURS', title: ['6 THEMES', '8 LANGUAGES'], clip: 'themes', src: [0.1, 5.6],
    cam: [{t: 0, x: 0.3, y: 0.35, z: 1}, {t: 1, x: 0.4, y: 0.35, z: 1.05}],
    chip: 'EN · TR · DE · ES · HI · ID · PT · RU'},
];

const STAGE = {x: 70, y: 640, w: 900, h: 820};

/** Vertical crop of a 16:9 recording: the recording's height fills the box (times z), the camera pans across it. */
const VStage: React.FC<{shot: Shot; status: AssetStatus}> = ({shot, status}) => {
  const f = useCurrentFrame();
  const {fps} = useVideoConfig();
  const len = (shot.to - shot.from) * fps;
  const u = f / len;
  const keys = shot.cam!;
  let k = keys[keys.length - 1];
  for (let i = 0; i < keys.length - 1; i++) {
    if (u < keys[i + 1].t) {
      const p = interpolate(u, [keys[i].t, keys[i + 1].t], [0, 1], {easing: detent, extrapolateLeft: 'clamp'});
      k = {t: u, x: keys[i].x + (keys[i + 1].x - keys[i].x) * p, y: keys[i].y + (keys[i + 1].y - keys[i].y) * p, z: keys[i].z + (keys[i + 1].z - keys[i].z) * p};
      break;
    }
  }
  if (u < keys[0].t) k = keys[0];
  const scale = (STAGE.h / 1080) * k.z;
  const cw = 1920 * scale;
  const ch = 1080 * scale;
  const tx = Math.min(0, Math.max(STAGE.w - cw, STAGE.w / 2 - k.x * cw));
  const ty = Math.min(0, Math.max(STAGE.h - ch, STAGE.h / 2 - k.y * ch));
  const [s0, s1] = shot.src!;
  const rate = (s1 - s0) / (shot.to - shot.from);
  const settle = tween(f, 0, 12);
  return (
    <div
      style={{
        position: 'absolute',
        left: STAGE.x,
        top: STAGE.y,
        width: STAGE.w,
        height: STAGE.h,
        overflow: 'hidden',
        background: '#0B0D12',
        border: `1px solid ${C.hair}`,
        boxShadow: `0 50px 120px rgba(0,0,0,0.85), -2px 0 0 ${rgba(C.crimson, 0.6)}, -40px 10px 90px -40px ${rgba(C.crimson, 0.6)}`,
        transform: `scale(${1.04 - 0.04 * settle})`,
      }}
    >
      <div style={{position: 'absolute', left: tx, top: ty, width: cw, height: ch}}>
        <ClipPlayer clip={shot.clip!} slot={len} status={status} trimStart={s0} rate={rate} />
      </div>
      <div style={{position: 'absolute', inset: 0, background: 'linear-gradient(180deg, rgba(255,255,255,0.05), transparent 20%)'}} />
    </div>
  );
};

const BigTitle: React.FC<{lines: string[]; top: number; size?: number; at?: number}> = ({lines, top, size = 132, at = 0}) => {
  const f = useCurrentFrame();
  return (
    <div style={{position: 'absolute', left: 70, right: 70, top}}>
      {lines.map((l, i) => {
        const a = at + i * 5;
        const p = tween(f, a, a + 16);
        return (
          <div
            key={l}
            style={{
              fontFamily: DISPLAY,
              fontWeight: 800,
              fontSize: size,
              lineHeight: 0.95,
              color: i === lines.length - 1 ? C.crimson : C.titanium,
              opacity: tween(f, a, a + 5, 0, 1, linear),
              transform: `translateY(${(1 - p) * 40}px)`,
              textShadow: '0 6px 40px rgba(0,0,0,0.8)',
            }}
          >
            {l}
          </div>
        );
      })}
    </div>
  );
};

const FeatureShot: React.FC<{shot: Shot; status: AssetStatus}> = ({shot, status}) => {
  const f = useCurrentFrame();
  return (
    <AbsoluteFill>
      <Label style={{position: 'absolute', left: 70, top: 280}} color={C.crimson} size={22}>
        {shot.kicker}
      </Label>
      <BigTitle lines={shot.title} top={320} />
      <VStage shot={shot} status={status} />
      {shot.chip && (
        <div style={{position: 'absolute', left: 70, top: STAGE.y + STAGE.h + 26, opacity: tween(f, 8, 16, 0, 1, linear)}}>
          <HudTag size={20} color={C.cyan}>
            {shot.chip}
          </HudTag>
        </div>
      )}
    </AbsoluteFill>
  );
};

/** 0 – CUT.reveal: the hook. */
const Hook: React.FC = () => {
  const f = useCurrentFrame();
  const j = (n: number) => (f % 7 < 2 && f < 40 ? (Math.sin(f * n) * 14) : 0);
  return (
    <AbsoluteFill style={{justifyContent: 'center'}}>
      <div style={{position: 'absolute', left: 70, right: 70, top: 560}}>
        <Label color={C.crimson} size={24} style={{opacity: tween(f, 0, 6, 0, 1, linear)}}>
          ACER NITRO / PREDATOR OWNERS
        </Label>
        {['STILL OPENING', 'NITROSENSE?'].map((l, i) => (
          <div
            key={l}
            style={{
              position: 'relative',
              fontFamily: DISPLAY,
              fontWeight: 800,
              fontSize: i ? 190 : 120,
              lineHeight: 0.95,
              marginTop: i ? 0 : 24,
              color: C.titanium,
              opacity: f >= 6 + i * 14 ? 1 : 0,
              transform: `translateX(${j(3 + i)}px)`,
              textShadow: `${-5 + j(2) / 3}px 0 ${rgba(C.cyan, 0.7)}, 5px 0 ${rgba(C.crimson, 0.8)}`,
            }}
          >
            {l}
          </div>
        ))}
        <div style={{fontFamily: MONO, fontSize: 30, color: C.muted, marginTop: 40, opacity: tween(f, Math.round(CUT.hookSub * 60) - 6, Math.round(CUT.hookSub * 60) + 6, 0, 1, linear)}}>just to change a fan speed?</div>
      </div>
    </AbsoluteFill>
  );
};

/** CUT.reveal – first shot: the answer. */
const Reveal: React.FC<{status: AssetStatus}> = ({status}) => {
  const f = useCurrentFrame();
  const p = tween(f, 0, 22);
  return (
    <AbsoluteFill style={{alignItems: 'center'}}>
      <AbsoluteFill style={{background: `radial-gradient(circle at 50% 40%, ${rgba(C.crimson, 0.22)}, transparent 40%)`}} />
      <div style={{position: 'absolute', top: 470, width: 300, height: 300, borderRadius: 60, overflow: 'hidden', transform: `scale(${1.3 - 0.3 * p})`, boxShadow: `0 0 140px ${rgba(C.crimson, 0.45)}`}}>
        <AssetImg img="logo" status={status} style={{width: '100%', height: '100%'}} />
      </div>
      <div style={{position: 'absolute', top: 840, width: '100%', textAlign: 'center', fontFamily: DISPLAY, fontWeight: 800, fontSize: 170, lineHeight: 1, color: C.titanium, letterSpacing: `${0.3 - 0.24 * tween(f, 6, 40)}em`, opacity: tween(f, 6, 14, 0, 1, linear)}}>
        NITROLITE
      </div>
      <div style={{position: 'absolute', top: 1060, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 18}}>
        {['FREE & OPEN SOURCE', '~73 MB · NO BLOAT', "WORKS WITHOUT ACER'S SOFTWARE"].map((t, i) => (
          <div key={t} style={{opacity: f >= 40 + i * 14 ? 1 : 0, transform: `scale(${1.25 - 0.25 * tween(f, 40 + i * 14, 54 + i * 14)})`}}>
            <HudTag size={26} color={i === 0 ? C.crimson : C.titanium}>
              {t}
            </HudTag>
          </div>
        ))}
      </div>
    </AbsoluteFill>
  );
};

/** CUT.end – end: end card. */
const EndCard: React.FC<{status: AssetStatus}> = ({status}) => {
  const f = useCurrentFrame();
  const show = (a: number) => ({opacity: tween(f, a, a + 10, 0, 1, linear), transform: `translateY(${(1 - tween(f, a, a + 18)) * 20}px)`});
  return (
    <AbsoluteFill style={{alignItems: 'center'}}>
      <AbsoluteFill style={{background: `radial-gradient(circle at 50% 36%, ${rgba(C.crimson, 0.2)}, transparent 40%)`}} />
      <div style={{position: 'absolute', top: 420, width: 260, height: 260, borderRadius: 52, overflow: 'hidden', boxShadow: `0 0 120px ${rgba(C.crimson, 0.4)}`, ...show(0)}}>
        <AssetImg img="logo" status={status} style={{width: '100%', height: '100%'}} />
      </div>
      <div style={{position: 'absolute', top: 740, fontFamily: DISPLAY, fontWeight: 800, fontSize: 160, color: C.titanium, letterSpacing: '0.06em', ...show(6)}}>NITROLITE</div>
      <div style={{position: 'absolute', top: 930, fontFamily: DISPLAY, fontWeight: 700, fontSize: 64, color: C.crimson, ...show(18)}}>FREE ON GITHUB</div>
      <div style={{position: 'absolute', top: 1050, fontFamily: MONO, fontSize: 30, color: C.titanium, padding: '14px 24px', border: '1px solid rgba(245,247,250,0.2)', borderLeft: `3px solid ${C.crimson}`, ...show(30)}}>
        {COPY.url}
      </div>
      <div style={{position: 'absolute', top: 1170, ...show(42)}}>
        <Label size={22} color={C.muted}>
          31 NITRO & PREDATOR MODELS · WINDOWS & LINUX
        </Label>
      </div>
    </AbsoluteFill>
  );
};

/** Platform overlays to keep clear of (approximate, worst case across TikTok / Reels / Shorts). */
const SafeZones: React.FC = () => (
  <AbsoluteFill style={{pointerEvents: 'none'}}>
    <div style={{position: 'absolute', left: 0, right: 0, top: 0, height: 230, background: 'rgba(56,189,248,0.25)'}} />
    <div style={{position: 'absolute', left: 0, right: 0, bottom: 0, height: 380, background: 'rgba(56,189,248,0.25)'}} />
    <div style={{position: 'absolute', right: 0, top: 820, width: 130, bottom: 380, background: 'rgba(56,189,248,0.25)'}} />
  </AbsoluteFill>
);

export type VerticalProps = {assets: AssetStatus | null; showSafeZones: boolean};

export const VerticalAd: React.FC<VerticalProps> = ({assets, showSafeZones}) => {
  const f = useCurrentFrame();
  const status = assets ?? {clips: {powerModes: null, fanCurve: null, rgb: null, gameOsd: null, batteryMux: null, themes: null, linux: null}, images: {logo: false, nitroKey: false}, audio: false};
  const s = (sec: number) => Math.round(sec * V.fps);
  const cuts = [0, CUT.reveal, ...CUT.shots, CUT.end];
  const flash = cuts.reduce((a, c) => {
    const d = f - s(c);
    return d >= 0 && d < 10 ? Math.max(a, 1 - d / 10) : a;
  }, 0);
  return (
    <AbsoluteFill style={{background: C.obsidian}}>
      <TelemetryGrid />
      <Sequence durationInFrames={s(CUT.reveal)} name="Hook">
        <Hook />
      </Sequence>
      <Sequence from={s(CUT.reveal)} durationInFrames={s(CUT.shots[0]) - s(CUT.reveal)} name="Reveal">
        <Reveal status={status} />
      </Sequence>
      {SHOTS.map((shot) => (
        <Sequence key={shot.kicker} from={s(shot.from)} durationInFrames={s(shot.to) - s(shot.from)} name={shot.kicker}>
          <FeatureShot shot={shot} status={status} />
        </Sequence>
      ))}
      <Sequence from={s(CUT.end)} name="End card">
        <EndCard status={status} />
      </Sequence>
      <AbsoluteFill style={{pointerEvents: 'none', background: `radial-gradient(ellipse 80% 60% at 50% 50%, transparent 60%, rgba(0,0,0,0.6))`}} />
      {flash > 0 && <AbsoluteFill style={{background: rgba(C.titanium, 0.5 * flash * flash)}} />}
      <FilmGrain opacity={0.03} />
      {showSafeZones && <SafeZones />}
    </AbsoluteFill>
  );
};
