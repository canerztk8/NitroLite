import React from 'react';
import {AbsoluteFill, Sequence, useCurrentFrame, useVideoConfig} from 'remotion';
import {AssetStatus} from '../config/assets';
import {COPY} from '../config/copy';
import {ACTS} from '../config/timeline';
import {CamKey, FootageStage} from '../components/FootageStage';
import {Callouts} from '../components/Overlays';
import {ActHeader, HudTag, Label, TelemetryGrid, Vignette} from '../primitives';
import {C, DISPLAY, linear, MONO, tween} from '../theme';

type P = {status: AssetStatus};
const slot = (k: keyof typeof ACTS) => ACTS[k][1] - ACTS[k][0];

const Shell: React.FC<{act: string; title: string; offset: number; children: React.ReactNode}> = ({act, title, offset, children}) => (
  <AbsoluteFill style={{background: C.obsidian}}>
    <TelemetryGrid />
    {children}
    <ActHeader act={act} title={title} offset={offset} />
    <Vignette strength={0.5} />
  </AbsoluteFill>
);

// ------------------------------------------------------------------------------------------------ II · power modes
const POWER_CAM: CamKey[] = [
  {t: 0, x: 0.5, y: 0.5, z: 1},
  {t: 2.6, x: 0.5, y: 0.5, z: 1},
  {t: 3.4, x: 0.27, y: 0.28, z: 1.5},
  {t: 5.5, x: 0.47, y: 0.28, z: 1.5},
  {t: 7.6, x: 0.67, y: 0.28, z: 1.5},
  {t: 9.7, x: 0.86, y: 0.29, z: 1.55},
  {t: 11.6, x: 0.86, y: 0.29, z: 1.55},
  {t: 13.4, x: 0.55, y: 0.4, z: 1.2},
];
export const ActPower: React.FC<P> = ({status}) => {
  const f = useCurrentFrame();
  const {fps} = useVideoConfig();
  const t = f / fps;
  const mode = t < 3.8 ? -1 : t < 5.9 ? 0 : t < 8 ? 1 : t < 10.1 ? 2 : 3;
  const c = COPY.power;
  return (
    <Shell act={c.act} title={c.title} offset={ACTS.power[0]}>
      <FootageStage clip="powerModes" slot={slot('power')} status={status} cam={POWER_CAM} />
      <div style={{position: 'absolute', right: 120, top: 112, display: 'flex', gap: 8}}>
        {c.modes.map((m, i) => (
          <span
            key={m}
            style={{
              fontFamily: MONO,
              fontSize: 14,
              letterSpacing: '0.18em',
              padding: '5px 10px',
              border: `1px solid ${i === mode ? C.crimson : C.hair}`,
              color: i === mode ? C.titanium : C.muted,
              background: i === mode ? 'rgba(230,46,46,0.18)' : 'transparent',
            }}
          >
            {m}
          </span>
        ))}
      </div>
      <Callouts
        items={[
          {at: 1.2, text: c.callouts[0], color: C.crimson},
          {at: 4.5, text: c.callouts[1]},
          {at: 8.5, text: c.callouts[2], color: C.cyan},
        ]}
      />
    </Shell>
  );
};

// ------------------------------------------------------------------------------------------------ III · fan curve
const FAN_CAM: CamKey[] = [
  {t: 0, x: 0.5, y: 0.45, z: 1},
  {t: 1.2, x: 0.56, y: 0.42, z: 1.45},
  {t: 4.2, x: 0.56, y: 0.42, z: 1.45},
  {t: 5.2, x: 0.5, y: 0.68, z: 1.35},
  {t: 11.5, x: 0.5, y: 0.68, z: 1.35},
  {t: 12.8, x: 0.78, y: 0.68, z: 1.75},
  {t: 16.5, x: 0.78, y: 0.68, z: 1.75},
  {t: 18, x: 0.6, y: 0.6, z: 1.25},
];
export const ActFan: React.FC<P> = ({status}) => {
  const f = useCurrentFrame();
  const c = COPY.fan;
  const b = 11.5 * 60;
  const p = tween(f, b, b + 22);
  return (
    <Shell act={c.act} title={c.title} offset={ACTS.fan[0]}>
      <FootageStage clip="fanCurve" slot={slot('fan')} status={status} cam={FAN_CAM} />
      {f >= b && (
        <div
          style={{
            position: 'absolute',
            left: 120,
            top: 520,
            width: 470,
            padding: '22px 26px',
            background: 'rgba(6,7,10,0.92)',
            border: `1px solid ${C.crimson}`,
            boxShadow: '0 0 60px rgba(230,46,46,0.35)',
            opacity: tween(f, b, b + 8, 0, 1, linear),
            transform: `translateX(${(1 - p) * -40}px)`,
          }}
        >
          <Label color={C.crimson} size={14}>
            {c.badge}
          </Label>
          <div style={{display: 'flex', flexDirection: 'column', gap: 6, marginTop: 14}}>
            {c.floor.map((l, i) => (
              <div key={l} style={{fontFamily: DISPLAY, fontWeight: 700, fontSize: 40, color: C.titanium, opacity: tween(f, b + 10 + i * 8, b + 18 + i * 8, 0, 1, linear)}}>
                {l}
              </div>
            ))}
          </div>
          <div style={{fontFamily: MONO, fontSize: 15, lineHeight: 1.5, color: C.muted, marginTop: 14}}>{c.note}</div>
        </div>
      )}
      <Callouts
        items={[
          {at: 1, text: c.callouts[0]},
          {at: 4.6, text: c.callouts[1], color: C.cyan},
          {at: 11.6, text: '85°C / 90°C / 95°C SAFETY FLOOR', color: C.crimson},
        ]}
      />
    </Shell>
  );
};

// ------------------------------------------------------------------------------------------------ IV · RGB
const RGB_CAM: CamKey[] = [
  {t: 0, x: 0.5, y: 0.4, z: 1.1},
  {t: 1, x: 0.55, y: 0.24, z: 1.55},
  {t: 8.5, x: 0.55, y: 0.24, z: 1.55},
  {t: 9.6, x: 0.45, y: 0.4, z: 1.3},
  {t: 14, x: 0.45, y: 0.4, z: 1.3},
  {t: 15.2, x: 0.55, y: 0.22, z: 1.65},
];
export const ActRgb: React.FC<P> = ({status}) => {
  const f = useCurrentFrame();
  const c = COPY.rgb;
  // effects are picked from 3.6 s into the act, roughly every 1.33 s at 2× speed
  const idx = Math.min(c.effects.length - 1, Math.floor((f / 60 - 3.6) / 1.33));
  return (
    <Shell act={c.act} title={c.title} offset={ACTS.rgb[0]}>
      <FootageStage clip="rgb" slot={slot('rgb')} status={status} cam={RGB_CAM} />
      <div style={{position: 'absolute', right: 120, top: 112, display: 'flex', gap: 6}}>
        {c.effects.map((e, i) => (
          <span
            key={e}
            style={{
              fontFamily: MONO,
              fontSize: 12,
              letterSpacing: '0.14em',
              padding: '4px 8px',
              border: `1px solid ${i === idx ? C.cyan : C.hair}`,
              color: i === idx ? C.obsidian : C.muted,
              background: i === idx ? C.cyan : 'transparent',
            }}
          >
            {e}
          </span>
        ))}
      </div>
      <Callouts
        items={[
          {at: 0.8, text: c.callouts[0], color: C.crimson},
          {at: 3.6, text: c.callouts[1], color: C.cyan},
          {at: 7, text: c.callouts[2]},
        ]}
      />
    </Shell>
  );
};

// ------------------------------------------------------------------------------------------------ V · games + OSD
const GAMES_CAM: CamKey[] = [
  {t: 0, x: 0.42, y: 0.78, z: 1.6},
  {t: 7.6, x: 0.42, y: 0.78, z: 1.6},
  {t: 8.8, x: 0.5, y: 0.5, z: 1.05},
  {t: 10.2, x: 0.5, y: 0.5, z: 1.05},
  {t: 11.2, x: 0.2, y: 0.12, z: 2.1},
  {t: 15.5, x: 0.2, y: 0.12, z: 2.1},
  {t: 17.5, x: 0.45, y: 0.35, z: 1.25},
];
export const ActGames: React.FC<P> = ({status}) => {
  const c = COPY.games;
  return (
    <Shell act={c.act} title={c.title} offset={ACTS.games[0]}>
      <FootageStage clip="gameOsd" slot={slot('games')} status={status} cam={GAMES_CAM} />
      <Callouts
        items={[
          {at: 0.8, text: c.callouts[0], color: C.crimson},
          {at: 3.5, text: c.callouts[1]},
          {at: 10.6, text: c.callouts[2], color: C.cyan},
        ]}
      />
    </Shell>
  );
};

// ------------------------------------------------------------------------------------------------ VI · battery, MUX, specs
const BATTERY_CAM: CamKey[] = [
  {t: 0, x: 0.5, y: 0.5, z: 1},
  {t: 0.8, x: 0.3, y: 0.24, z: 1.55},
  {t: 3, x: 0.3, y: 0.24, z: 1.55},
  {t: 4, x: 0.5, y: 0.55, z: 1.3},
  {t: 7.2, x: 0.5, y: 0.55, z: 1.3},
  {t: 8.2, x: 0.84, y: 0.6, z: 1.9},
  {t: 11, x: 0.84, y: 0.6, z: 1.9},
  {t: 12.5, x: 0.55, y: 0.45, z: 1.15},
];
export const ActBattery: React.FC<P> = ({status}) => {
  const f = useCurrentFrame();
  const c = COPY.battery;
  return (
    <Shell act={c.act} title={c.title} offset={ACTS.battery[0]}>
      <FootageStage clip="batteryMux" slot={slot('battery')} status={status} cam={BATTERY_CAM} x={416} y={172} w={1088} />
      <div style={{position: 'absolute', left: 120, right: 120, top: 830, display: 'flex', gap: 18}}>
        {c.specs.map((s, i) => {
          const a = 60 + i * 24;
          const p = tween(f, a, a + 20);
          return (
            <div
              key={s.big}
              style={{
                flex: 1,
                padding: '16px 22px',
                border: `1px solid ${i === 2 ? C.crimson : C.hair}`,
                background: 'rgba(18,22,31,0.85)',
                opacity: tween(f, a, a + 8, 0, 1, linear),
                transform: `translateY(${(1 - p) * 20}px)`,
              }}
            >
              <div style={{fontFamily: DISPLAY, fontWeight: 800, fontSize: 46, lineHeight: 1, color: i === 2 ? C.crimson : C.titanium}}>{s.big}</div>
              <Label style={{marginTop: 8}}>{s.small}</Label>
            </div>
          );
        })}
      </div>
      <Callouts
        y={990}
        x={120}
        size={14}
        items={[
          {at: 0.8, text: c.callouts[0], color: C.cyan},
          {at: 3.6, text: c.callouts[1]},
          {at: 7.8, text: c.callouts[2], color: C.crimson},
        ]}
      />
    </Shell>
  );
};

// ------------------------------------------------------------------------------------------------ VII · themes, languages, platforms
const THEMES_CAM: CamKey[] = [
  {t: 0, x: 0.5, y: 0.5, z: 1.0},
  {t: 12, x: 0.45, y: 0.4, z: 1.12},
];
export const ActThemes: React.FC<P> = ({status}) => {
  const f = useCurrentFrame();
  const {fps} = useVideoConfig();
  const c = COPY.themes;
  const t = f / fps;
  const idx = Math.min(c.shots.length - 1, Math.floor((t + 0.1) / c.shotSeconds));
  const shot = c.shots[idx];
  const lang = shot.startsWith('LANG') ? shot.slice(-2) : 'EN';
  const hasLinux = status.clips.linux !== null;
  const linuxAt = 7 * fps;
  return (
    <Shell act={c.act} title={c.title} offset={ACTS.themes[0]}>
      <Sequence durationInFrames={hasLinux ? linuxAt : undefined} layout="none">
        <FootageStage clip="themes" slot={slot('themes')} status={status} cam={THEMES_CAM} />
      </Sequence>
      {hasLinux && (
        <Sequence from={linuxAt} layout="none">
          <FootageStage clip="linux" slot={slot('themes') - linuxAt} status={status} cam={THEMES_CAM} />
        </Sequence>
      )}
      <div style={{position: 'absolute', right: 120, top: 112, display: 'flex', gap: 6, alignItems: 'center'}}>
        <HudTag color={C.cyan} size={13} style={{marginRight: 10}}>
          {hasLinux && f >= linuxAt ? 'LINUX' : shot}
        </HudTag>
        {c.langs.map((l) => (
          <span
            key={l}
            style={{
              fontFamily: MONO,
              fontSize: 13,
              letterSpacing: '0.1em',
              padding: '4px 7px',
              border: `1px solid ${l === lang ? C.crimson : C.hair}`,
              color: l === lang ? C.titanium : C.muted,
              background: l === lang ? 'rgba(230,46,46,0.2)' : 'transparent',
            }}
          >
            {l}
          </span>
        ))}
      </div>
      <Callouts
        items={[
          {at: 0.6, text: '6 THEMES', color: C.cyan},
          {at: 5.6, text: '8 LANGUAGES', color: C.crimson},
          {at: 8.4, text: c.callouts[0]},
          {at: 10, text: c.callouts[1]},
        ]}
      />
    </Shell>
  );
};
