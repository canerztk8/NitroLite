import {AUDIO} from './assets';

export const FPS = 60;
export const TOTAL = 7200;
export const W = 1920;
export const H = 1080;

export const ACTS = {
  ignition: [0, 840],
  power: [840, 1800],
  fan: [1800, 2880],
  rgb: [2880, 3960],
  games: [3960, 5040],
  battery: [5040, 5880],
  themes: [5880, 6600],
  finale: [6600, 7200],
} as const;
export type ActKey = keyof typeof ACTS;
export const span = (k: ActKey) => ({from: ACTS[k][0], durationInFrames: ACTS[k][1] - ACTS[k][0]});

export const BEAT = 60 / AUDIO.bpm;
/** Video frame of the n-th beat after the first impact at 0:12.0. */
export const beatFrame = (n: number) => Math.round((12 + n * BEAT) * FPS);

/** Impact cuts: the key press on the track's first hit, then phrase starts nearest 0:46 and 1:18, and the final cut. */
export const IMPACTS = [beatFrame(0), beatFrame(64), beatFrame(128), ACTS.finale[0]];

export const KEY_PRESS = {start: 660, hit: 720};
export const FADE_IN = 90; // 1.5 s
export const FADE_OUT_START = 7020; // 1:57, 3.0 s to the end
