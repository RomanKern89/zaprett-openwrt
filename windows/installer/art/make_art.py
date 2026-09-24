"""Draws the installer artwork in the style of the zaprett mark (same geometry as
windows/src/Zaprett.Ui/Assets/zaprett.svg):
    Banner.bmp  493 x 58   top banner of the WixUI dialogs (WixUIBannerBmp); title text is drawn over its left part
    Dialog.bmp  493 x 312  background of the welcome / finish dialogs (WixUIDialogBmp); text over its right part
Run after changing the design:  PYTHONUTF8=1 python windows/installer/art/make_art.py   (needs Pillow)
"""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
SS = 4  # supersampling

C1 = (14, 124, 134)   # gradient of the mark, top left
C2 = (27, 77, 184)    # bottom right
LIGHT = (246, 249, 251)
RADIUS = 0.22
LINES = (((0.27, 0.285), (0.73, 0.285)), ((0.27, 0.715), (0.73, 0.715)),
         ((0.70, 0.31), (0.545, 0.465)), ((0.455, 0.535), (0.30, 0.69)))
STROKE = 0.105


def gradient(w, h, c1, c2):
    img = Image.new('RGB', (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            t = (x / max(w - 1, 1) + y / max(h - 1, 1)) / 2
            px[x, y] = tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))
    return img


def draw_mark(img, x0, y0, side, rounded=True, background=True):
    """Draws the mark (supersampled) into img with its top-left corner at (x0, y0)."""
    big = side * SS
    layer = Image.new('RGBA', (big, big), (0, 0, 0, 0))
    if background:
        tile = gradient(big, big, C1, C2).convert('RGBA')
        mask = Image.new('L', (big, big), 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), radius=int(RADIUS * big) if rounded else 0,
                                               fill=255)
        layer.paste(tile, (0, 0), mask)
    d = ImageDraw.Draw(layer)
    width = max(1, int(STROKE * big))
    for (ax, ay), (bx, by) in LINES:
        p1, p2 = (ax * big, ay * big), (bx * big, by * big)
        d.line((p1, p2), fill=(255, 255, 255, 255), width=width)
        r = width / 2
        for cx, cy in (p1, p2):
            d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(255, 255, 255, 255))
    small = layer.resize((side, side), Image.LANCZOS)
    img.paste(small, (x0, y0), small)


def banner():
    w, h = 493, 58
    img = Image.new('RGB', (w, h), LIGHT)
    # thin accent line at the bottom, the mark on the right (the title text sits on the left)
    img.paste(gradient(w, 3, C1, C2), (0, h - 3))
    draw_mark(img, w - 50, 6, 42)
    return img


def dialog():
    w, h = 493, 312
    img = Image.new('RGB', (w, h), (255, 255, 255))
    panel = 164   # WixUI puts the text of the welcome / finish dialogs to the right of this panel
    img.paste(gradient(panel, h, C1, C2), (0, 0))
    draw_mark(img, (panel - 104) // 2, 70, 104, background=False)
    d = ImageDraw.Draw(img)
    d.line(((panel, 0), (panel, h)), fill=(220, 226, 232), width=1)
    return img


def main():
    for name, make in (('Banner.bmp', banner), ('Dialog.bmp', dialog)):
        path = os.path.join(HERE, name)
        make().save(path, format='BMP')
        print('%s %dx%d' % (path, *Image.open(path).size))


if __name__ == '__main__':
    main()
