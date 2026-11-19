import React from 'react';
import {Composition} from 'remotion';
import {probeAssets} from './config/assets';
import {FPS, H, TOTAL, W} from './config/timeline';
import {Trailer, TrailerProps} from './Trailer';
import {V, VerticalAd, VerticalProps} from './vertical/VerticalAd';
import {M, MasterProps, MasterTrailer, probeBroll} from './master/MasterTrailer';
import './theme';

export const RemotionRoot: React.FC = () => (
  <>
  <Composition
    id="NitroLiteEpic"
    component={Trailer}
    durationInFrames={TOTAL}
    fps={FPS}
    width={W}
    height={H}
    defaultProps={{assets: null} satisfies TrailerProps}
    // checks which files exist in public/ (and how long each clip is) before preview and before every render
    calculateMetadata={async ({props}) => ({props: {...props, assets: await probeAssets()}})}
  />
  {/* 24 s vertical ad for Shorts / TikTok / Reels, rendered silent */}
  <Composition
    id="NitroLiteVertical"
    component={VerticalAd}
    durationInFrames={V.frames}
    fps={V.fps}
    width={V.w}
    height={V.h}
    defaultProps={{assets: null, showSafeZones: false} satisfies VerticalProps}
    calculateMetadata={async ({props}) => ({props: {...props, assets: await probeAssets()}})}
  />
  {/* 72 s master trailer: AI B-roll + 1.2.7 screen recordings */}
  <Composition
    id="NitroLiteMaster"
    component={MasterTrailer}
    durationInFrames={M.frames}
    fps={M.fps}
    width={M.w}
    height={M.h}
    defaultProps={{assets: null, broll: null} satisfies MasterProps}
    calculateMetadata={async ({props}) => ({props: {...props, assets: await probeAssets(), broll: await probeBroll()}})}
  />
  </>
);
