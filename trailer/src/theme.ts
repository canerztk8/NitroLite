import {loadFont as loadBarlow} from '@remotion/google-fonts/BarlowCondensed';
import {loadFont as loadInter} from '@remotion/google-fonts/Inter';
import {loadFont as loadMono} from '@remotion/google-fonts/JetBrainsMono';
import {Easing, interpolate} from 'remotion';

export const DISPLAY = loadBarlow('normal', {weights: ['600', '700', '800'], subsets: ['latin']}).fontFamily;
export const INTER = loadInter('normal', {weights: ['400', '500', '600'], subsets: ['latin']}).fontFamily;
export const MONO = loadMono('normal', {weights: ['400', '500', '700'], subsets: ['latin']}).fontFamily;
export const tabular = {fontVariantNumeric: 'tabular-nums' as const};

export const C = {
  obsidian: '#06070A',
  gunmetal: '#12161F',
  gunmetalHi: '#1A202C',
  crimson: '#E62E2E',
  cyan: '#38BDF8',
  titanium: '#F5F7FA',
  muted: '#7D8698',
  hair: 'rgba(245,247,250,0.14)',
} as const;

/** cubic-bezier(0.16, 1, 0.3, 1): fast travel, hard settle, no overshoot. */
export const detent = Easing.bezier(0.16, 1, 0.3, 1);
export const linear = (t: number) => t;

export function tween(f: number, f0: number, f1: number, from = 0, to = 1, easing: (t: number) => number = detent) {
  return interpolate(f, [f0, f1], [from, to], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp', easing});
}

export function rgba(hex: string, a: number) {
  const p = parseInt(hex.slice(1), 16);
  return `rgba(${(p >> 16) & 255},${(p >> 8) & 255},${p & 255},${a})`;
}
