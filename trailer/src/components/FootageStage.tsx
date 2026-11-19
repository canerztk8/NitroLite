import React from 'react';
import {interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {AssetStatus, CLIPS, ClipKey} from '../config/assets';
import {Brackets, Label} from '../primitives';
import {C, detent, rgba, tween} from '../theme';
import {ClipPlayer} from './Media';

/** Camera key: at `t` seconds into the act, centre the source point (x, y) (0..1 of the recording) at zoom z. */
export type CamKey = {t: number; x: number; y: number; z: number};

function camAt(keys: CamKey[], s: number) {
  if (s <= keys[0].t) return keys[0];
  for (let i = 0; i < keys.length - 1; i++) {
    const a = keys[i];
    const b = keys[i + 1];
    if (s < b.t) {
      const u = interpolate(s, [a.t, b.t], [0, 1], {easing: detent});
      return {t: s, x: a.x + (b.x - a.x) * u, y: a.y + (b.y - a.y) * u, z: a.z + (b.z - a.z) * u};
    }
  }
  return keys[keys.length - 1];
}

/**
 * Floating dark glass/metal viewport around a real recording, with a slow camera, crimson rim light, corner
 * brackets and a source tag. The cut-in settles over the first 14 frames.
 */
export const FootageStage: React.FC<{
  clip: ClipKey;
  slot: number;
  status: AssetStatus;
  cam: CamKey[];
  x?: number;
  y?: number;
  w?: number;
}> = ({clip, slot, status, cam, x = 304, y = 176, w = 1312}) => {
  const f = useCurrentFrame();
  const {fps} = useVideoConfig();
  const h = Math.round((w * 9) / 16);
  const k = camAt(cam, f / fps);
  const fx = Math.min(1 - 0.5 / k.z, Math.max(0.5 / k.z, k.x));
  const fy = Math.min(1 - 0.5 / k.z, Math.max(0.5 / k.z, k.y));
  const settle = tween(f, 0, 14);
  const drift = Math.sin(f / 90) * 3;
  return (
    <>
      <div
        style={{
          position: 'absolute',
          left: x,
          top: y + drift,
          width: w,
          height: h,
          overflow: 'hidden',
          background: '#0B0D12',
          border: `1px solid ${C.hair}`,
          boxShadow: [
            '0 60px 140px rgba(0,0,0,0.85)',
            `-2px 0 0 ${rgba(C.crimson, 0.55)}`,
            `-40px 10px 90px -40px ${rgba(C.crimson, 0.55)}`,
            `0 -1px 0 ${rgba(C.titanium, 0.12)}`,
          ].join(','),
          transform: `scale(${1.035 - 0.035 * settle})`,
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
            transform: `translate(${w / 2}px, ${h / 2}px) scale(${k.z}) translate(${-fx * w}px, ${-fy * h}px)`,
          }}
        >
          <ClipPlayer clip={clip} slot={slot} status={status} />
        </div>
        {/* glass: top sheen and a soft inner edge */}
        <div
          style={{
            position: 'absolute',
            inset: 0,
            pointerEvents: 'none',
            background: 'linear-gradient(180deg, rgba(255,255,255,0.06) 0%, transparent 18%, transparent 85%, rgba(0,0,0,0.35) 100%)',
            boxShadow: 'inset 0 0 0 1px rgba(255,255,255,0.04), inset 0 0 60px rgba(0,0,0,0.5)',
          }}
        />
      </div>
      <Brackets x={x - 14} y={y - 14 + drift} w={w + 28} h={h + 28} color={C.titanium} opacity={0.5} />
      <Label style={{position: 'absolute', left: x, top: y + h + 22 + drift}}>{`SOURCE · ${CLIPS[clip].src.toUpperCase()}`}</Label>
      <Label style={{position: 'absolute', left: x + w - 300, width: 300, textAlign: 'right', top: y + h + 22 + drift}} color={C.cyan}>
        {`LIVE CAPTURE · ${k.z.toFixed(2)}×`}
      </Label>
    </>
  );
};
