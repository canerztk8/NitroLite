import React from 'react';
import {AbsoluteFill, Audio, interpolate, Sequence, staticFile, useCurrentFrame} from 'remotion';
import {Act1Ignition} from './acts/Act1Ignition';
import {ActBattery, ActFan, ActGames, ActPower, ActRgb, ActThemes} from './acts/FeatureActs';
import {Finale} from './acts/Finale';
import {AUDIO, AssetStatus} from './config/assets';
import {FADE_IN, FADE_OUT_START, FPS, IMPACTS, span, TOTAL} from './config/timeline';
import {FilmGrain, ImpactFlash} from './primitives';
import {C} from './theme';

export type TrailerProps = {assets: AssetStatus | null};

const EMPTY: AssetStatus = {
  clips: {powerModes: null, fanCurve: null, rgb: null, gameOsd: null, batteryMux: null, themes: null, linux: null},
  images: {logo: false, nitroKey: false},
  audio: false,
};

export const Trailer: React.FC<TrailerProps> = ({assets}) => {
  const f = useCurrentFrame();
  const status = assets ?? EMPTY;
  // a short zoom kick on every impact
  const kick = IMPACTS.reduce((k, at) => {
    const d = f - at;
    return d >= 0 && d < 24 ? Math.max(k, 1 - d / 24) : k;
  }, 0);
  return (
    <AbsoluteFill style={{background: C.obsidian}}>
      <AbsoluteFill style={{transform: `scale(${1 + 0.018 * kick})`}}>
        <Sequence {...span('ignition')} name="I · Ignition">
          <Act1Ignition status={status} />
        </Sequence>
        <Sequence {...span('power')} name="II · Power modes">
          <ActPower status={status} />
        </Sequence>
        <Sequence {...span('fan')} name="III · Fan curves">
          <ActFan status={status} />
        </Sequence>
        <Sequence {...span('rgb')} name="IV · RGB">
          <ActRgb status={status} />
        </Sequence>
        <Sequence {...span('games')} name="V · Games + OSD">
          <ActGames status={status} />
        </Sequence>
        <Sequence {...span('battery')} name="VI · Battery + MUX">
          <ActBattery status={status} />
        </Sequence>
        <Sequence {...span('themes')} name="VII · Themes + languages">
          <ActThemes status={status} />
        </Sequence>
        <Sequence {...span('finale')} name="Finale">
          <Finale status={status} />
        </Sequence>
      </AbsoluteFill>
      {IMPACTS.map((at) => (
        <ImpactFlash key={at} at={at} />
      ))}
      <FilmGrain opacity={0.03} />
      {status.audio && (
        <Audio
          src={staticFile(AUDIO.src)}
          trimBefore={Math.round(AUDIO.startAt * FPS)}
          volume={(fr) => interpolate(fr, [0, FADE_IN, FADE_OUT_START, TOTAL], [0, 1, 1, 0], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'})}
        />
      )}
    </AbsoluteFill>
  );
};
