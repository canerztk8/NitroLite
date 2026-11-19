import React from 'react';
import {AbsoluteFill, Freeze, Img, OffthreadVideo, Sequence, staticFile, useVideoConfig} from 'remotion';
import {AssetStatus, CLIPS, ClipKey, IMAGES, ImageKey} from '../config/assets';
import {C, MONO} from '../theme';

/** Plain plate shown in place of a clip that is not in public/clips yet. Never a drawn imitation of the app. */
const MissingClip: React.FC<{src: string}> = ({src}) => (
  <AbsoluteFill style={{background: '#0B0D12', alignItems: 'center', justifyContent: 'center'}}>
    <div style={{fontFamily: MONO, fontSize: 22, letterSpacing: '0.2em', color: C.muted}}>CLIP MISSING</div>
    <div style={{fontFamily: MONO, fontSize: 18, color: C.crimson, marginTop: 12}}>public/{src}</div>
  </AbsoluteFill>
);

/**
 * A real screen recording filling its box. If the clip (after trimStart, at its rate) is shorter than the slot,
 * it plays to its last frame and holds there for the rest of the slot.
 */
export const ClipPlayer: React.FC<{clip: ClipKey; slot: number; status: AssetStatus; trimStart?: number; rate?: number}> = ({
  clip,
  slot,
  status,
  trimStart,
  rate,
}) => {
  const {fps} = useVideoConfig();
  const c = {...CLIPS[clip], ...(trimStart !== undefined ? {trimStart} : {}), ...(rate !== undefined ? {rate} : {})};
  const dur = status.clips[clip];
  if (dur === null) return <MissingClip src={c.src} />;
  const avail = Math.max(1, Math.floor(((dur - c.trimStart) / c.rate) * fps) - 2);
  const video = (
    <OffthreadVideo
      src={staticFile(c.src)}
      trimBefore={Math.round(c.trimStart * fps)}
      playbackRate={c.rate}
      muted
      style={{width: '100%', height: '100%', display: 'block'}}
    />
  );
  if (avail >= slot) return <AbsoluteFill>{video}</AbsoluteFill>;
  return (
    <AbsoluteFill>
      <Sequence durationInFrames={avail} layout="none">
        {video}
      </Sequence>
      <Sequence from={avail} layout="none">
        <Freeze frame={avail - 1}>{video}</Freeze>
      </Sequence>
    </AbsoluteFill>
  );
};

/** A real image from public/assets, falling back to another real asset (the logo) when it is missing. */
export const AssetImg: React.FC<{img: ImageKey; status: AssetStatus; fallback?: ImageKey; style?: React.CSSProperties}> = ({
  img,
  status,
  fallback = 'logo',
  style,
}) => {
  const use = status.images[img] ? img : status.images[fallback] ? fallback : null;
  if (!use) return null;
  return <Img src={staticFile(IMAGES[use])} style={style} />;
};
