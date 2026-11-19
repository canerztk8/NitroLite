"""Mix the voiceover and the music under the silent vertical render.

Usage:  python scripts/mix-vertical.py <voiceover.mp3> <music.mp3> <silent.mp4> <out-dir>

Line spans below were measured on the ElevenLabs take with silencedetect; the scene cuts in
src/vertical/VerticalAd.tsx (CUT) were computed from the same numbers, with each cut snapped to a kick in the track.
Writes <out-dir>/nitrolite-vertical-30s.mp4 (voice + music) and nitrolite-vertical-30s-voice-only.mp4 (add the
same song inside TikTok / Instagram instead, so the platform licenses it).
"""
import os, subprocess, sys

vo, music, silent, out = sys.argv[1:5]

LINES = [(0.0, 5.19), (5.61, 10.71), (11.30, 14.69), (15.06, 18.98), (19.32, 22.78), (23.12, 26.78), (27.21, 29.56), (29.87, 33.44)]
STARTS = [0.1, 4.938, 9.703, 12.905, 16.737, 20.199, 23.651, 25.976]  # where each line begins in the video
SPEED = 1.10        # voice tempo (pitch kept)
SONG_AT = 50.052    # song time at video 0: the drop (54.89 s) lands on the logo reveal (4.838 s)
TOTAL = 29.69
PRE = 0.04          # keep a little air before each line

parts, labels = [], []
for k, (a, b) in enumerate(LINES):
    end = min(b + 0.15, LINES[k + 1][0] - 0.02) if k + 1 < len(LINES) else b
    delay = int(round((STARTS[k] - PRE / SPEED) * 1000))
    parts.append(f"[0:a]atrim={max(0, a - PRE)}:{end},asetpts=PTS-STARTPTS,atempo={SPEED},"
                 f"afade=t=in:d=0.02,afade=t=out:st={(end - max(0, a - PRE)) / SPEED - 0.05:.3f}:d=0.05,adelay={delay}[l{k}]")
    labels.append(f"[l{k}]")
voice = (";".join(parts) + ";" + "".join(labels) +
         f"amix=inputs={len(LINES)}:normalize=0,highpass=f=90,acompressor=threshold=0.1:ratio=3:attack=5:release=120,"
         f"volume=2.2,apad,atrim=0:{TOTAL}")

full = (voice + ",asplit=2[v][sc];"
        f"[1:a]atrim={SONG_AT}:{SONG_AT + TOTAL},asetpts=PTS-STARTPTS,aformat=channel_layouts=stereo,volume=0.55,"
        f"afade=t=in:d=0.2,afade=t=out:st={TOTAL - 1.4}:d=1.4[m];"
        "[m][sc]sidechaincompress=threshold=0.02:ratio=8:attack=15:release=380:makeup=1[md];"
        "[v]aformat=channel_layouts=stereo[vs];[vs][md]amix=inputs=2:normalize=0,"
        "loudnorm=I=-14:TP=-1.5:LRA=9,aresample=48000,alimiter=limit=0.84:attack=2:release=60:level=disabled[out]")
solo = voice + ",aformat=channel_layouts=stereo,loudnorm=I=-14:TP=-1.5:LRA=9,aresample=48000,alimiter=limit=0.84:attack=2:release=60:level=disabled[out]"


def run(graph, name, with_music):
    wav = os.path.join(out, name + ".wav")
    ins = ["-i", vo] + (["-i", music] if with_music else [])
    subprocess.run(["ffmpeg", "-v", "error", "-y", *ins, "-filter_complex", graph, "-map", "[out]", "-t", str(TOTAL), wav], check=True)
    mp4 = os.path.join(out, name + ".mp4")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", silent, "-i", wav, "-map", "0:v", "-map", "1:a", "-c:v", "copy",
                    "-c:a", "aac", "-b:a", "320k", "-t", str(TOTAL), "-movflags", "+faststart", mp4], check=True)
    os.remove(wav)
    print("wrote", mp4)


run(full, "nitrolite-vertical-30s", True)
run(solo, "nitrolite-vertical-30s-voice-only", False)
