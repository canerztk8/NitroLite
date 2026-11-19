import React from 'react';
import {AbsoluteFill, useCurrentFrame} from 'remotion';
import {AssetStatus} from '../config/assets';
import {COPY} from '../config/copy';
import {KEY_PRESS} from '../config/timeline';
import {AssetImg} from '../components/Media';
import {SubBassLine} from '../components/Overlays';
import {HudTag, Label} from '../primitives';
import {C, INTER, linear, rgba, tween} from '../theme';

const Line: React.FC<{text: string; accent: string; from: number; to: number; y: number}> = ({text, accent, from, to, y}) => {
  const f = useCurrentFrame();
  const p = tween(f, from, from + 54);
  const op = tween(f, from, from + 30, 0, 1, linear) * (1 - tween(f, to - 24, to, 0, 1, linear));
  const ls = 0.6 - 0.28 * p;
  const i = text.indexOf(accent);
  return (
    <div
      style={{
        position: 'absolute',
        top: y,
        width: '100%',
        textAlign: 'center',
        fontFamily: INTER,
        fontWeight: 500,
        fontSize: 34,
        letterSpacing: `${ls}em`,
        paddingLeft: `${ls}em`,
        color: C.titanium,
        opacity: op,
        transform: `translateY(${(1 - p) * 10}px)`,
        whiteSpace: 'nowrap',
        textShadow: '0 2px 28px rgba(0,0,0,0.95)',
      }}
    >
      {text.slice(0, i)}
      <span style={{color: C.crimson}}>{accent}</span>
      {text.slice(i + accent.length)}
    </div>
  );
};

/** 0:00–0:14. Logo from darkness, the Nitro key in macro light, the press on the first hit, then the stamp. */
export const Act1Ignition: React.FC<{status: AssetStatus}> = ({status}) => {
  const f = useCurrentFrame();
  const hit = KEY_PRESS.hit;

  // logo: 0–300
  const logoIn = tween(f, 20, 140, 0, 1, linear);
  const sweep = tween(f, 60, 200, -0.4, 1.4, linear);
  const logoOut = tween(f, 270, 300, 0, 1, linear);

  // key: 300–840
  const keyIn = tween(f, 300, 380, 0, 1, linear);
  const zoom = 1 + 0.32 * tween(f, 300, KEY_PRESS.start, 0, 1, linear);
  const press = tween(f, KEY_PRESS.start, hit, 0, 1, (t) => t * t * t); // slow, then bottoms out on the hit
  const after = f >= hit ? tween(f, hit, hit + 40) : 0;
  const ring = f >= hit ? tween(f, hit, hit + 50, 0, 1, linear) : 0;
  const light = 0.55 + 0.45 * Math.sin(f / 40);

  return (
    <AbsoluteFill style={{background: C.obsidian}}>
      {status.audio && <SubBassLine y={1000} opacity={tween(f, 0, 90, 0, 1, linear) * (1 - tween(f, 280, 320, 0, 1, linear) * 0.6)} />}

      {/* the real logo, emerging with a crimson light sweep */}
      {f < 300 && (
        <AbsoluteFill style={{alignItems: 'center', justifyContent: 'center', opacity: logoIn * (1 - logoOut)}}>
          <div
            style={{
              position: 'relative',
              width: 220,
              height: 220,
              marginTop: -150,
              transform: `scale(${0.94 + 0.06 * logoIn})`,
              filter: `brightness(${0.35 + 0.65 * logoIn})`,
              borderRadius: 44,
              overflow: 'hidden',
              boxShadow: `0 0 ${120 * logoIn}px ${rgba(C.crimson, 0.28 * logoIn)}`,
            }}
          >
            <AssetImg img="logo" status={status} style={{width: '100%', height: '100%'}} />
            <div
              style={{
                position: 'absolute',
                inset: 0,
                background: `linear-gradient(110deg, transparent ${(sweep - 0.25) * 100}%, ${rgba(C.crimson, 0.55)} ${sweep * 100}%, transparent ${(sweep + 0.25) * 100}%)`,
                mixBlendMode: 'screen',
              }}
            />
          </div>
        </AbsoluteFill>
      )}
      <Line text={COPY.act1Title} accent="FULL POWER." from={90} to={300} y={640} />

      {/* the Nitro key in dark macro light; it compresses slowly and bottoms out on the beat */}
      {f >= 300 && (
        <AbsoluteFill style={{alignItems: 'center', justifyContent: 'center', opacity: keyIn}}>
          <div
            style={{
              position: 'relative',
              width: status.images.nitroKey ? 520 : 300,
              height: status.images.nitroKey ? 520 : 300,
              transform: `scale(${zoom * (1 - 0.06 * press + 0.04 * after * (1 - after))}) translateY(${16 * press}px) scaleY(${1 - 0.07 * press * (1 - after)})`,
              filter: `brightness(${0.6 + 0.4 * light + 0.5 * (f >= hit ? 1 - after : 0)}) contrast(1.1)`,
            }}
          >
            <AssetImg img="nitroKey" status={status} style={{width: '100%', height: '100%', objectFit: 'contain', borderRadius: status.images.nitroKey ? 0 : 90}} />
          </div>
          {/* shockwave */}
          {ring > 0 && ring < 1 && (
            <div
              style={{
                position: 'absolute',
                width: 200 + 1700 * ring,
                height: 200 + 1700 * ring,
                borderRadius: '50%',
                border: `${3 + 6 * (1 - ring)}px solid ${rgba(C.crimson, 1 - ring)}`,
                boxShadow: `0 0 ${60 * (1 - ring)}px ${rgba(C.crimson, 0.8 * (1 - ring))}`,
              }}
            />
          )}
        </AbsoluteFill>
      )}
      {f >= 300 && (
        <AbsoluteFill
          style={{
            pointerEvents: 'none',
            background: `radial-gradient(circle at 50% ${46 - 6 * light}%, transparent 12%, rgba(6,7,10,0.55) 30%, rgba(6,7,10,0.97) 62%)`,
          }}
        />
      )}
      <Line text={COPY.act1Key} accent="CONTROL." from={360} to={650} y={880} />

      {/* the stamp after the press */}
      {f >= hit + 8 && (
        <AbsoluteFill style={{alignItems: 'center', justifyContent: 'flex-end', paddingBottom: 110}}>
          <div style={{display: 'flex', gap: 16}}>
            {COPY.act1Stamp.map((t, i) => {
              const a = hit + 8 + i * 9;
              const p = tween(f, a, a + 14);
              return (
                <div key={t} style={{opacity: f >= a ? 1 : 0, transform: `scale(${1.35 - 0.35 * p})`}}>
                  <HudTag color={i === 0 ? C.crimson : C.titanium} size={20} fill={i === 0 ? 'rgba(230,46,46,0.12)' : undefined}>
                    {t}
                  </HudTag>
                </div>
              );
            })}
          </div>
        </AbsoluteFill>
      )}
      {f >= 300 && f < hit && status.images.nitroKey && (
        <Label style={{position: 'absolute', left: 120, bottom: 70, opacity: keyIn}} color={C.muted}>
          NITRO KEY  ·  OPENS NITROLITE
        </Label>
      )}
    </AbsoluteFill>
  );
};
