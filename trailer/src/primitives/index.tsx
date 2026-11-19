import React from 'react';
import {AbsoluteFill, useCurrentFrame} from 'remotion';
import {H, W} from '../config/timeline';
import {C, DISPLAY, linear, MONO, rgba, tabular, tween} from '../theme';

/** Full-contrast grain at 3 % opacity, reseeded every frame. */
export const FilmGrain: React.FC<{opacity?: number}> = ({opacity = 0.03}) => {
  const f = useCurrentFrame();
  return (
    <AbsoluteFill style={{pointerEvents: 'none', opacity}}>
      <svg width={W} height={H}>
        <filter id="grain" x="0" y="0" width="100%" height="100%">
          <feTurbulence type="fractalNoise" baseFrequency="0.85" numOctaves={2} seed={f % 127} stitchTiles="stitch" />
          <feColorMatrix type="matrix" values="3 0 0 0 -1  3 0 0 0 -1  3 0 0 0 -1  0 0 0 0 1" />
        </filter>
        <rect width={W} height={H} filter="url(#grain)" />
      </svg>
    </AbsoluteFill>
  );
};

/** 1 px telemetry grid with registration crosses. */
export const TelemetryGrid: React.FC<{opacity?: number}> = ({opacity = 1}) => {
  const crosses: React.ReactNode[] = [];
  for (let x = 240; x < W; x += 240)
    for (let y = 120; y < H; y += 240)
      crosses.push(<path key={`${x}-${y}`} d={`M${x - 6} ${y + 0.5}H${x + 7}M${x + 0.5} ${y - 6}V${y + 7}`} stroke={C.titanium} strokeOpacity={0.14} />);
  return (
    <AbsoluteFill style={{pointerEvents: 'none', opacity}}>
      <svg width={W} height={H} shapeRendering="crispEdges">
        <defs>
          <pattern id="g-min" width={48} height={48} patternUnits="userSpaceOnUse" y={24}>
            <path d="M47.5 0V48M0 47.5H48" fill="none" stroke={C.titanium} strokeOpacity={0.025} />
          </pattern>
          <pattern id="g-maj" width={240} height={240} patternUnits="userSpaceOnUse" y={120}>
            <path d="M239.5 0V240M0 239.5H240" fill="none" stroke={C.titanium} strokeOpacity={0.05} />
          </pattern>
        </defs>
        <rect width={W} height={H} fill="url(#g-min)" />
        <rect width={W} height={H} fill="url(#g-maj)" />
        {crosses}
      </svg>
    </AbsoluteFill>
  );
};

export const Vignette: React.FC<{strength?: number; rim?: number}> = ({strength = 0.65, rim = 1}) => (
  <AbsoluteFill
    style={{
      pointerEvents: 'none',
      background: [
        `radial-gradient(ellipse 75% 70% at 50% 50%, transparent 55%, rgba(0,0,0,${strength}) 100%)`,
        `radial-gradient(ellipse 60% 45% at 0% 100%, rgba(230,46,46,${0.08 * rim}), transparent 70%)`,
      ].join(','),
    }}
  />
);

export const Label: React.FC<{children: React.ReactNode; size?: number; color?: string; style?: React.CSSProperties}> = ({
  children,
  size = 13,
  color = C.muted,
  style,
}) => <div style={{fontFamily: MONO, fontSize: size, letterSpacing: '0.2em', color, whiteSpace: 'nowrap', ...tabular, ...style}}>{children}</div>;

/** Hairline-bordered monospace telemetry badge. */
export const HudTag: React.FC<{children: React.ReactNode; color?: string; size?: number; fill?: string; style?: React.CSSProperties}> = ({
  children,
  color = C.titanium,
  size = 16,
  fill = 'rgba(6,7,10,0.78)',
  style,
}) => (
  <div
    style={{
      display: 'inline-flex',
      alignItems: 'center',
      gap: size * 0.7,
      padding: `${size * 0.55}px ${size * 0.95}px`,
      border: `1px solid ${color}`,
      background: fill,
      fontFamily: MONO,
      fontWeight: 500,
      fontSize: size,
      letterSpacing: '0.16em',
      color,
      whiteSpace: 'nowrap',
      ...tabular,
      ...style,
    }}
  >
    <span style={{width: size * 0.42, height: size * 0.42, background: color, flex: 'none'}} />
    {children}
  </div>
);

export const Brackets: React.FC<{x: number; y: number; w: number; h: number; len?: number; color?: string; opacity?: number}> = ({
  x,
  y,
  w,
  h,
  len = 22,
  color = C.titanium,
  opacity = 0.55,
}) => (
  <svg style={{position: 'absolute', left: 0, top: 0, overflow: 'visible', pointerEvents: 'none'}} width={1} height={1}>
    <path
      d={`M${x} ${y + len}V${y}H${x + len}M${x + w - len} ${y}H${x + w}V${y + len}M${x + w} ${y + h - len}V${y + h}H${x + w - len}M${x + len} ${y + h}H${x}V${y + h - len}`}
      fill="none"
      stroke={color}
      strokeOpacity={opacity}
      strokeWidth={1.5}
    />
  </svg>
);

/** Act index, condensed title and a running timecode. Frames are relative to the act. */
export const ActHeader: React.FC<{act: string; title: string; offset: number}> = ({act, title, offset}) => {
  const f = useCurrentFrame();
  const p = tween(f, 4, 30);
  const g = f + offset;
  const s = g / 60;
  return (
    <>
      <div style={{position: 'absolute', left: 120, top: 46, opacity: tween(f, 2, 10, 0, 1, linear)}}>
        <Label color={C.crimson} size={15}>
          {act}
        </Label>
        <div
          style={{
            fontFamily: DISPLAY,
            fontWeight: 800,
            fontSize: 70,
            lineHeight: 1,
            marginTop: 8,
            color: C.titanium,
            letterSpacing: `${0.12 - 0.08 * p}em`,
            transform: `translateX(${(1 - p) * -28}px)`,
            textTransform: 'uppercase',
          }}
        >
          {title}
        </div>
      </div>
      <Label style={{position: 'absolute', right: 120, top: 50}} color={C.titanium}>
        {`T+${String(Math.floor(s / 60)).padStart(2, '0')}:${(s % 60).toFixed(2).padStart(5, '0')}`}
        <span style={{color: C.muted}}>{`   F ${String(g).padStart(4, '0')}`}</span>
      </Label>
    </>
  );
};

/** White-to-crimson impact flash, `at` relative to the enclosing sequence. */
export const ImpactFlash: React.FC<{at: number; strength?: number}> = ({at, strength = 1}) => {
  const f = useCurrentFrame() - at;
  if (f < 0 || f > 16) return null;
  const a = (1 - f / 16) * strength;
  return (
    <AbsoluteFill style={{pointerEvents: 'none'}}>
      <AbsoluteFill style={{background: f < 3 ? rgba(C.titanium, 0.85 * a) : rgba(C.crimson, 0.32 * a)}} />
      <div
        style={{
          position: 'absolute',
          left: 0,
          right: 0,
          top: 540 - 160 * a,
          height: 320 * a,
          background: `linear-gradient(180deg, transparent, ${rgba(C.crimson, 0.7 * a)} 40%, ${rgba(C.titanium, a)} 50%, ${rgba(C.crimson, 0.7 * a)} 60%, transparent)`,
          filter: 'blur(8px)',
        }}
      />
    </AbsoluteFill>
  );
};
