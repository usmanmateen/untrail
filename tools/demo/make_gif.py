"""Builds docs/untrail-demo.gif from the frames recorded by record-demo.ps1.

    python tools/demo/make_gif.py <framesDir> docs/untrail-demo.gif [--max-hold-ms 1400] [--width 1280]

Frames keep their real timing. Unchanged stretches (waiting on a scan, for example) are shortened to at most
--max-hold-ms; nothing is added or redrawn. A single shared palette keeps the flat UI colours exact.
"""
import argparse
import os

from PIL import Image, ImageChops


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("frames")
    ap.add_argument("out")
    ap.add_argument("--max-hold-ms", type=int, default=1400)
    ap.add_argument("--end-hold-ms", type=int, default=3000)
    ap.add_argument("--width", type=int, default=1280)
    a = ap.parse_args()

    times = [int(t) for t in open(os.path.join(a.frames, "times.txt")).read().split()]
    files = [os.path.join(a.frames, "%05d.bmp" % (i + 1)) for i in range(len(times))]
    n = min(len(files), sum(1 for f in files if os.path.exists(f)))
    times, files = times[:n], files[:n]
    durations = [times[i + 1] - times[i] for i in range(len(times) - 1)] + [a.end_hold_ms]

    kept = []  # [image, duration_ms]
    prev = None
    for path, d in zip(files, durations):
        im = Image.open(path).convert("RGB")
        if a.width != im.width:
            im = im.resize((a.width, round(im.height * a.width / im.width)), Image.LANCZOS)
        if prev is not None and ImageChops.difference(im, prev).getbbox() is None:
            kept[-1][1] += d
        else:
            kept.append([im, d])
            prev = im
    for k in kept:
        k[1] = min(k[1], a.max_hold_ms)
    kept[-1][1] = a.end_hold_ms

    # One palette for every frame, built from frames spread across the recording.
    w, h = kept[0][0].size
    picks = [kept[i * (len(kept) - 1) // 7][0] for i in range(8)]
    mosaic = Image.new("RGB", (w, h * len(picks)))
    for i, im in enumerate(picks):
        mosaic.paste(im, (0, h * i))
    palette = mosaic.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    frames = [im.quantize(palette=palette, dither=Image.Dither.NONE) for im, _ in kept]

    frames[0].save(a.out, save_all=True, append_images=frames[1:], duration=[d for _, d in kept],
                   loop=0, optimize=True, disposal=1)
    total = sum(d for _, d in kept) / 1000
    print("%s: %d frames, %.1f s, %.2f MB, %dx%d" % (a.out, len(frames), total, os.path.getsize(a.out) / 1e6, w, h))


if __name__ == "__main__":
    main()
