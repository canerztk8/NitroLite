import React from 'react';
import {AbsoluteFill, useCurrentFrame} from 'remotion';
import {AssetStatus} from '../config/assets';
import {COPY} from '../config/copy';
import {AssetImg} from '../components/Media';
import {C, DISPLAY, linear, MONO, rgba, tween} from '../theme';

/** 1:50–2:00. Hard cut to obsidian: the real logo, then the signature lines one after another. */
export const Finale: React.FC<{status: AssetStatus}> = ({status}) => {
  const f = useCurrentFrame();
  const glow = 0.6 + 0.4 * Math.sin(f / 30);
  const t = tween(f, 14, 70);
  const show = (a: number) => ({
    opacity: tween(f, a, a + 16, 0, 1, linear),
    transform: `translateY(${(1 - tween(f, a, a + 26)) * 14}px)`,
  });
  return (
    <AbsoluteFill style={{background: C.obsidian, alignItems: 'center'}}>
      <AbsoluteFill style={{background: `radial-gradient(circle at 50% 34%, ${rgba(C.crimson, 0.16 * glow)}, transparent 32%)`}} />
      <div
        style={{
          position: 'absolute',
          top: 150,
          width: 230,
          height: 230,
          borderRadius: 46,
          overflow: 'hidden',
          boxShadow: `0 0 ${90 + 50 * glow}px ${rgba(C.crimson, 0.35)}, 0 30px 80px rgba(0,0,0,0.8)`,
          opacity: tween(f, 0, 6, 0, 1, linear),
        }}
      >
        <AssetImg img="logo" status={status} style={{width: '100%', height: '100%'}} />
      </div>
      <div
        style={{
          position: 'absolute',
          top: 432,
          width: '100%',
          textAlign: 'center',
          fontFamily: DISPLAY,
          fontWeight: 800,
          fontSize: 150,
          lineHeight: 1,
          color: C.titanium,
          letterSpacing: `${0.5 - 0.36 * t}em`,
          paddingLeft: `${0.5 - 0.36 * t}em`,
          opacity: tween(f, 14, 28, 0, 1, linear),
        }}
      >
        {COPY.finale.title}
      </div>
      <div style={{position: 'absolute', top: 612, width: 640 * tween(f, 40, 80), height: 2, background: C.crimson, boxShadow: `0 0 18px ${rgba(C.crimson, 0.7)}`}} />
      {COPY.finale.lines.map((l, i) => (
        <div
          key={l}
          style={{position: 'absolute', top: 652 + i * 46, fontFamily: MONO, fontSize: 22, letterSpacing: '0.2em', color: i ? C.titanium : C.muted, whiteSpace: 'pre', ...show(70 + i * 24)}}
        >
          {l}
        </div>
      ))}
      <div
        style={{
          position: 'absolute',
          top: 770,
          fontFamily: MONO,
          fontWeight: 500,
          fontSize: 32,
          letterSpacing: '0.06em',
          color: C.titanium,
          padding: '12px 26px',
          border: '1px solid rgba(245,247,250,0.2)',
          borderLeft: `3px solid ${C.crimson}`,
          ...show(124),
        }}
      >
        {COPY.url}
      </div>
    </AbsoluteFill>
  );
};
