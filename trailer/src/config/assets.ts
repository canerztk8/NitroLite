import {getVideoMetadata} from '@remotion/media-utils';
import {staticFile} from 'remotion';

/**
 * Every file the trailer uses, relative to public/. Nothing on screen is drawn in place of these: a missing clip
 * shows a plain "missing" plate, a missing Nitro key photo falls back to the real logo.
 *
 * trimStart: seconds skipped at the head of the recording. rate: playback speed inside its act. If a clip runs out
 * before its act ends it holds on its last frame (see ClipPlayer).
 */
export const CLIPS = {
  powerModes: {src: 'clips/01-power-modes.mp4', trimStart: 0, rate: 1.15},
  fanCurve: {src: 'clips/02-fan-curve.mp4', trimStart: 0.8, rate: 1.3},
  rgb: {src: 'clips/03-rgb-effects.mp4', trimStart: 4, rate: 2},
  gameOsd: {src: 'clips/04-game-profiles-osd.mp4', trimStart: 0.8, rate: 1.38},
  batteryMux: {src: 'clips/05-battery-mux.mp4', trimStart: 0.6, rate: 1.32},
  themes: {src: 'clips/06-themes-languages.mp4', trimStart: 0.1, rate: 1},
  linux: {src: 'clips/07-linux.mp4', trimStart: 0, rate: 1},
} as const;
export type ClipKey = keyof typeof CLIPS;

export const IMAGES = {
  logo: 'assets/logo.png',
  nitroKey: 'assets/nitro-key.png',
} as const;
export type ImageKey = keyof typeof IMAGES;

/** Max Brhon – Cyberpunk [NCS Release]. startAt puts its first impact (35.0 s) on the key press at 0:12.0. */
export const AUDIO = {src: 'audio/ncs-track.mp3', startAt: 23.0, bpm: 110} as const;

export type AssetStatus = {
  /** Duration in seconds, or null when the file is missing or unreadable. */
  clips: Record<ClipKey, number | null>;
  images: Record<ImageKey, boolean>;
  audio: boolean;
};

const withTimeout = <T,>(p: Promise<T>, ms: number, fallback: T) =>
  Promise.race([p, new Promise<T>((r) => setTimeout(() => r(fallback), ms))]);

const exists = (path: string) =>
  withTimeout(
    fetch(staticFile(path))
      .then((r) => r.ok)
      .catch(() => false),
    8000,
    false,
  );

/** Runs in calculateMetadata before Studio preview and before every render. */
export async function probeAssets(): Promise<AssetStatus> {
  const clipEntries = await Promise.all(
    (Object.keys(CLIPS) as ClipKey[]).map(async (k) => {
      const ok = await exists(CLIPS[k].src);
      if (!ok) return [k, null] as const;
      const d = await withTimeout(
        getVideoMetadata(staticFile(CLIPS[k].src))
          .then((m) => m.durationInSeconds)
          .catch(() => null),
        15000,
        null,
      );
      return [k, d] as const;
    }),
  );
  const imageEntries = await Promise.all((Object.keys(IMAGES) as ImageKey[]).map(async (k) => [k, await exists(IMAGES[k])] as const));
  return {
    clips: Object.fromEntries(clipEntries) as AssetStatus['clips'],
    images: Object.fromEntries(imageEntries) as AssetStatus['images'],
    audio: await exists(AUDIO.src),
  };
}
