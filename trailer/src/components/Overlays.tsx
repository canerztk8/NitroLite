import {useAudioData, visualizeAudio} from '@remotion/media-utils';
import React from 'react';
import {staticFile, useCurrentFrame, useVideoConfig} from 'remotion';
import {AUDIO} from '../config/assets';
import {W} from '../config/timeline';
import {HudTag} from '../primitives';
import {C, linear, tween} from '../theme';

/** Telemetry chips that slide in one after another along a row. `at` is seconds into the act. */
export const Callouts: React.FC<{items: {at: number; text: string; color?: string}[]; x?: number; y?: number; size?: number}> = ({
  items,
  x = 304,
  y = 978,
  size = 15,
}) => {
  const f = useCurrentFrame();
  const {fps} = useVideoConfig();
  return (
    <div style={{position: 'absolute', left: x, top: y, display: 'flex', gap: 14}}>
      {items.map((it, i) => {
        const a = it.at * fps;
        if (f < a) return null;
        const p = tween(f, a, a + 18);
        return (
          <div key={i} style={{opacity: tween(f, a, a + 6, 0, 1, linear), transform: `translateX(${(1 - p) * -26}px)`}}>
            <HudTag color={it.color ?? C.titanium} size={size}>
              {it.text}
            </HudTag>
          </div>
        );
      })}
    </div>
  );
};

/** Sub-bass line driven by the soundtrack's lowest bands. Render only when the audio file exists. */
export const SubBassLine: React.FC<{y?: number; opacity?: number; gain?: number}> = ({y = 1000, opacity = 1, gain = 1}) => {
  const f = useCurrentFrame();
  const {fps} = useVideoConfig();
  const audio = useAudioData(staticFile(AUDIO.src));
  let level = 0;
  if (audio) {
    const bins = visualizeAudio({fps, frame: f + Math.round(AUDIO.startAt * fps), audioData: audio, numberOfSamples: 32});
    level = (bins[0] + bins[1] + bins[2]) / 3;
  }
  const amp = 4 + Math.min(1, level * 2.2) * 70 * gain;
  const n = 260;
  let d = '';
  for (let i = 0; i <= n; i++) {
    const u = i / n;
    const env = Math.pow(Math.sin(Math.PI * u), 0.9);
    const v = Math.sin(2 * Math.PI * u * 2.4 - f * 0.07) * 0.7 + Math.sin(2 * Math.PI * u * 6.3 + f * 0.11) * 0.3;
    d += `${i ? 'L' : 'M'}${(u * W).toFixed(1)} ${(y + v * amp * env).toFixed(2)}`;
  }
  return (
    <svg width={W} height={1080} style={{position: 'absolute', inset: 0, opacity, pointerEvents: 'none'}}>
      <filter id="sub-glow" x="-5%" y="-50%" width="110%" height="200%">
        <feGaussianBlur stdDeviation="6" />
      </filter>
      <path d={`M0 ${y + 0.5}H${W}`} stroke={C.titanium} strokeOpacity={0.07} />
      <path d={d} fill="none" stroke={C.crimson} strokeWidth={6} strokeOpacity={0.35} filter="url(#sub-glow)" />
      <path d={d} fill="none" stroke={C.crimson} strokeWidth={1.5} />
    </svg>
  );
};
