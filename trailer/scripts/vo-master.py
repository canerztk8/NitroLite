"""Cut the master-trailer voiceover into one file per phrase, ready for MasterTrailer.tsx.

Usage:  python scripts/vo-master.py   (reads public/audio/vo-master.mp3, writes public/audio/vo/NN.wav)

Spans were measured on the ElevenLabs take (Brian, s45 sim75 v4) with silencedetect. The whole take is levelled once
(high-pass, gentle compression, +14 dB, limiter) so every phrase keeps the same loudness, then each phrase is cut with
a little air on both sides and played 5 % faster, pitch kept, so the closing lines fit before 1:12.
"""
import os, subprocess

ROOT = os.path.join(os.path.dirname(__file__), '..', 'public', 'audio')
SRC = os.path.join(ROOT, 'vo-master.mp3')
OUT = os.path.join(ROOT, 'vo')
SPEED = 1.05
PRE, POST = 0.03, 0.10

SPANS = [
    (0.00, 2.58), (3.12, 5.42),                        # built for full power / waiting on bloatware
    (6.21, 6.96), (7.60, 8.25),                        # one key / override
    (8.85, 9.29), (9.80, 10.48), (10.77, 11.54), (12.03, 12.61), (13.09, 13.88),  # quiet balanced performance turbo / one click
    (14.31, 16.68), (17.06, 19.96),                    # fan curve / safety floor
    (20.36, 22.39), (22.77, 24.59),                    # four zones eight effects / your keyboard
    (25.06, 28.22), (28.67, 31.27),                    # every game / live temps
    (31.71, 33.82), (34.13, 35.20), (35.52, 36.34), (36.71, 38.06),  # battery+gpu / themes / languages / windows+linux
    (38.45, 39.27), (39.76, 42.10), (42.41, 43.42), (43.86, 44.37), (44.72, 46.10),  # nitrolite / 24 MB, no telemetry / no kernel driver / free / open source
]

os.makedirs(OUT, exist_ok=True)
lev = os.path.join(OUT, '_levelled.wav')
subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', SRC, '-af',
                'highpass=f=70,acompressor=threshold=0.08:ratio=2.5:attack=5:release=150,volume=5.0,alimiter=limit=0.89:level=disabled',
                '-ar', '48000', lev], check=True)
for i, (a, b) in enumerate(SPANS):
    nxt = SPANS[i + 1][0] if i + 1 < len(SPANS) else b + 1
    s, e = max(0.0, a - PRE), min(b + POST, nxt - 0.02)
    d = (e - s) / SPEED
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', lev, '-af',
                    f'atrim={s}:{e},asetpts=PTS-STARTPTS,atempo={SPEED},afade=t=in:d=0.015,afade=t=out:st={d - 0.04:.3f}:d=0.04',
                    os.path.join(OUT, f'{i:02d}.wav')], check=True)
os.remove(lev)
print(f'wrote {len(SPANS)} phrases to {OUT}')
