#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Draws the zaprett mark (same geometry as zaprett.svg) into the .ico/.png files of the interface:
#   zaprett.ico         application icon, 16..256 px
#   tray-{on,off,warn,error}.ico  notification area icons with a state dot, 16..48 px
#   zaprett-256.png     window/title bar image
# Rendering is supersampled 8x with Pillow, so small sizes stay crisp. Run: python make-icons.py
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
SS = 8  # supersampling factor

# Geometry in units of the icon side (0..1), mirrored in zaprett.svg.
RADIUS = 0.22
TOP = ((0.27, 0.285), (0.73, 0.285))
BOTTOM = ((0.27, 0.715), (0.73, 0.715))
DIAG_A = ((0.70, 0.31), (0.545, 0.465))  # upper half of the diagonal
DIAG_B = ((0.455, 0.535), (0.30, 0.69))  # lower half, after the gap: the "split" packet
STROKE = 0.105

GRADIENTS = {
    'on': ((14, 124, 134), (27, 77, 184)),
    'off': ((110, 116, 124), (72, 78, 88)),
}
DOTS = {'on': (46, 204, 113), 'warn': (255, 185, 0), 'error': (232, 17, 35)}


def gradient(size, c1, c2):
    img = Image.new('RGBA', (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3)) + (255,)
    return img


def mark(px, colors, dot=None):
    size = px * SS
    base = gradient(size, *colors)
    mask = Image.new('L', (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=int(RADIUS * size), fill=255)
    out = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    out.paste(base, (0, 0), mask)
    d = ImageDraw.Draw(out)
    w = max(SS, int(STROKE * size))
    for (a, b) in (TOP, BOTTOM, DIAG_A, DIAG_B):
        p1 = (a[0] * size, a[1] * size)
        p2 = (b[0] * size, b[1] * size)
        d.line((p1, p2), fill=(255, 255, 255, 255), width=w)
        for p in (p1, p2):
            d.ellipse((p[0] - w / 2, p[1] - w / 2, p[0] + w / 2, p[1] + w / 2), fill=(255, 255, 255, 255))
    if dot:
        r = 0.2 * size
        cx, cy = size - r - 0.01 * size, size - r - 0.01 * size
        ring = r + 0.06 * size
        # a transparent ring around the dot keeps it readable on any taskbar colour
        d.ellipse((cx - ring, cy - ring, cx + ring, cy + ring), fill=(0, 0, 0, 0))
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=DOTS[dot] + (255,))
    return out.resize((px, px), Image.LANCZOS)


def save_ico(path, frames):
    frames = sorted(frames, key=lambda f: f.size[0], reverse=True)
    frames[0].save(path, format='ICO', sizes=[f.size for f in frames], append_images=frames[1:])


def main():
    app_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    save_ico(os.path.join(HERE, 'zaprett.ico'), [mark(s, GRADIENTS['on']) for s in app_sizes])
    mark(256, GRADIENTS['on']).save(os.path.join(HERE, 'zaprett-256.png'))
    tray_sizes = [16, 20, 24, 32, 40, 48]
    for state, colors, dot in (('on', GRADIENTS['on'], 'on'), ('off', GRADIENTS['off'], None),
                               ('warn', GRADIENTS['on'], 'warn'), ('error', GRADIENTS['off'], 'error')):
        save_ico(os.path.join(HERE, 'tray-%s.ico' % state), [mark(s, colors, dot) for s in tray_sizes])


if __name__ == '__main__':
    main()
