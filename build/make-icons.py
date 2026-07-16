#!/usr/bin/env python3
"""Regenerates every icon asset from design/icon-source.png.

Produces:
  design/icon-1024.png                  full-bleed tile (master)
  src/CertConvert/Assets/icon-256.png   in-app logo (full-bleed)
  src/CertConvert/Assets/certconvert.ico  Windows/window icon (full-bleed)
  src/CertConvert/Assets/CertConvert.icns macOS icon — squircle at 82% of the
      canvas with a soft shadow, per Apple's icon grid, so it sits at the same
      visual size as every other Dock icon.

Run with --site to (re)generate only the product-website Open Graph image
(site/img/og.png) from the existing design/icon-1024.png master — this leaves
the app icons untouched.
"""
import os
import sys
import subprocess
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)

TILE_RADIUS_RATIO = 0.224
SHIELD_SCALE = 0.78         # shield height as a fraction of tile height
MACOS_TILE_RATIO = 824/1024 # Apple icon grid: artwork occupies ~82% of canvas


def extract_shield(src: Image.Image):
    """Bounding box of the saturated (indigo) artwork, and the tile fill colour."""
    px = src.load()
    w, h = src.size
    xs, ys = [], []
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            r, g, b = px[x, y]
            if max(r, g, b) - min(r, g, b) > 60:
                xs.append(x)
                ys.append(y)
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    sample = src.crop((x0 - 60, (y0 + y1)//2 - 20, x0 - 20, (y0 + y1)//2 + 20))
    tile_rgb = max(sample.getcolors(maxcolors=10000), key=lambda c: c[0])[1]
    pad = 24
    return src.crop((x0 - pad, y0 - pad, x1 + pad, y1 + pad)), tile_rgb


def make_tile(shield: Image.Image, tile_rgb, size: int) -> Image.Image:
    """Full-bleed rounded tile with the shield centred."""
    tile = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(tile)
    d.rounded_rectangle([0, 0, size - 1, size - 1],
                        radius=int(size * TILE_RADIUS_RATIO), fill=tile_rgb + (255,))
    th = int(size * SHIELD_SCALE)
    tw = int(shield.width * th / shield.height)
    s = shield.resize((tw, th), Image.LANCZOS)
    tile.paste(s, ((size - tw)//2, (size - th)//2))
    return tile


def make_macos_icon(tile_1024: Image.Image) -> Image.Image:
    """Tile inset to Apple's icon grid on a 1024 canvas, with a soft shadow."""
    canvas = Image.new('RGBA', (1024, 1024), (0, 0, 0, 0))
    art = int(1024 * MACOS_TILE_RATIO)
    off = (1024 - art) // 2

    shadow_mask = Image.new('L', (1024, 1024), 0)
    d = ImageDraw.Draw(shadow_mask)
    d.rounded_rectangle([off, off + 12, off + art, off + 12 + art],
                        radius=int(art * TILE_RADIUS_RATIO), fill=80)
    shadow_mask = shadow_mask.filter(ImageFilter.GaussianBlur(18))
    canvas.paste((0, 0, 0), (0, 0), shadow_mask)

    small = tile_1024.resize((art, art), Image.LANCZOS)
    canvas.alpha_composite(small, (off, off))
    return canvas


# ---------------------------------------------------------------------------
# Product-website assets
# ---------------------------------------------------------------------------

SITE_FONT = '/System/Library/Fonts/SFNS.ttf'
OG_BG_TOP = (49, 46, 129)      # indigo-900
OG_BG_BOTTOM = (79, 70, 229)   # indigo-600 (brand accent)


def _vertical_gradient(size, top, bottom):
    w, h = size
    grad = Image.new('RGB', (1, h))
    for y in range(h):
        t = y / max(1, h - 1)
        grad.putpixel((0, y), tuple(int(top[c] + (bottom[c] - top[c]) * t) for c in range(3)))
    return grad.resize((w, h))


def make_site_og(tile_1024: Image.Image):
    """1200x630 Open Graph card: app tile + name + strapline on a brand background."""
    W, H = 1200, 630
    canvas = _vertical_gradient((W, H), OG_BG_TOP, OG_BG_BOTTOM).convert('RGBA')

    # App tile, left of centre, with a soft drop shadow.
    tile_px = 300
    tx, ty = 120, (H - tile_px) // 2
    tile = tile_1024.convert('RGBA').resize((tile_px, tile_px), Image.LANCZOS)
    shadow = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.rounded_rectangle([tx + 8, ty + 16, tx + tile_px + 8, ty + tile_px + 16],
                         radius=int(tile_px * 0.224), fill=(0, 0, 0, 90))
    shadow = shadow.filter(ImageFilter.GaussianBlur(20))
    canvas.alpha_composite(shadow)
    canvas.alpha_composite(tile, (tx, ty))

    d = ImageDraw.Draw(canvas)
    name_font = ImageFont.truetype(SITE_FONT, 104)
    strap_font = ImageFont.truetype(SITE_FONT, 50)
    sub_font = ImageFont.truetype(SITE_FONT, 33)

    text_x = tx + tile_px + 70
    d.text((text_x, 208), 'CertConvert', font=name_font, fill=(255, 255, 255, 255))
    d.text((text_x, 330), 'Certificate Toolbox', font=strap_font, fill=(199, 210, 254, 255))
    d.text((text_x, 402), 'Convert, chain, inspect and generate',
           font=sub_font, fill=(165, 180, 252, 255))
    d.text((text_x, 442), 'X.509 certificates — entirely offline.',
           font=sub_font, fill=(165, 180, 252, 255))

    os.makedirs('site/img', exist_ok=True)
    canvas.convert('RGB').save('site/img/og.png')
    print('wrote site/img/og.png')


if '--site' in sys.argv:
    make_site_og(Image.open('design/icon-1024.png'))
    sys.exit(0)


src = Image.open('design/icon-source.png').convert('RGB')
shield, tile_rgb = extract_shield(src)
print(f'shield {shield.size}, tile colour {tile_rgb}')

tile = make_tile(shield, tile_rgb, 1024)
tile.save('design/icon-1024.png')
tile.resize((256, 256), Image.LANCZOS).save('src/CertConvert/Assets/icon-256.png')
tile.save('src/CertConvert/Assets/certconvert.ico',
          sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])

macos = make_macos_icon(tile)
macos.save('design/icon-macos-1024.png')
os.makedirs('design/CertConvert.iconset', exist_ok=True)
for pt in (16, 32, 128, 256, 512):
    macos.resize((pt, pt), Image.LANCZOS).save(
        f'design/CertConvert.iconset/icon_{pt}x{pt}.png')
    macos.resize((pt*2, pt*2), Image.LANCZOS).save(
        f'design/CertConvert.iconset/icon_{pt}x{pt}@2x.png')
subprocess.run(['iconutil', '-c', 'icns', 'design/CertConvert.iconset',
                '-o', 'src/CertConvert/Assets/CertConvert.icns'], check=True)
import shutil
shutil.rmtree('design/CertConvert.iconset')
print('wrote icon-1024.png, icon-256.png, certconvert.ico, CertConvert.icns')
