"""Compose README artwork from fresh, unaltered KITN UI renders.

Requires Pillow, fontTools and resvg-py. Source captures live in docs/artwork/screens.
The Lucide cat comes directly from the vendored SVG; the wordmark is outlined.
"""

from pathlib import Path
import json
from io import BytesIO
import xml.etree.ElementTree as ET

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
import resvg_py

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "docs/artwork"
SCREENS = OUT / "screens"
SIZE = (3200, 1600)
GOLD = "#f5c44e"
FONT = Path("C:/Windows/Fonts/bahnschrift.ttf")


def logo_paths():
    font = instantiateVariableFont(TTFont(FONT), {"wght": 600, "wdth": 100})
    glyphs = font.getGlyphSet()
    cmap = font.getBestCmap()
    scale = 100 / font["head"].unitsPerEm
    tracking = 13
    width = sum(font["hmtx"][cmap[ord(c)]][0] * scale for c in "KITN") + 3 * tracking
    x = (512 - width) / 2
    pen = SVGPathPen(glyphs)
    for char in "KITN":
        glyphs[cmap[ord(char)]].draw(TransformPen(pen, (scale, 0, 0, -scale, x, 422)))
        x += font["hmtx"][cmap[ord(char)]][0] * scale + tracking
    icon = ET.parse(ROOT / "KITN/Assets/Icons/cat.svg").getroot()
    return [p.attrib["d"] for p in icon], pen.getCommands()


def make_logo(color, svg):
    # Render the original SVG strokes as continuous curves with proper joins.
    colored = svg.replace('stroke="currentColor"', f'stroke="{color}"').replace('fill="currentColor"', f'fill="{color}"')
    png = resvg_py.svg_to_bytes(svg_string=colored, width=2048, height=2048, skip_system_fonts=True)
    return Image.open(BytesIO(png)).convert("RGBA").resize((1024, 1024), Image.Resampling.LANCZOS)


def write_logos():
    paths, wordmark = logo_paths()
    body = '\n'.join(f'<path d="{path}"/>' for path in paths)
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" fill="none">
  <title>KITN</title>
  <desc>Lucide cat above the KITN wordmark. Lucide icon licensed under ISC.</desc>
  <g transform="translate(96 40) scale({320 / 24})" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    {body}
  </g>
  <path d="{wordmark}" fill="currentColor"/>
</svg>
'''
    (OUT / "kitn-logo.svg").write_text(svg, encoding="utf-8")
    for name, color in (("black", "#14171b"), ("white", "#f9fafb"), ("gold", GOLD)):
        make_logo(color, svg).save(OUT / f"kitn-logo-{name}.png", optimize=True)
    logo = make_logo("#14171b", svg)
    preview = Image.new("RGBA", logo.size, "white")
    preview.alpha_composite(logo)
    preview.convert("RGB").save(OUT / "kitn-logo-preview.png", optimize=True)
    preview.resize((384, 384), Image.Resampling.LANCZOS).convert("RGB").save(OUT / "kitn-logo-preview-small.png", optimize=True)


def load_screen(name):
    image = Image.open(SCREENS / f"{name}.png").convert("RGBA")
    # The native renderer's canvas surrounds each window. Remove only that canvas.
    bg = Image.new("RGB", image.size, (10, 14, 20))
    box = ImageChops.difference(image.convert("RGB"), bg).getbbox()
    image = image.crop(box)
    mask = Image.new("L", image.size)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, image.width-1, image.height-1), radius=6, fill=255)
    image.putalpha(mask)
    return image


def paste_shadow(canvas, image, x, y, opacity=60, blur=26, offset=16):
    pad = blur * 3
    shadow = Image.new("RGBA", (image.width + 2*pad, image.height + 2*pad))
    alpha = Image.new("L", shadow.size)
    alpha.paste(image.getchannel("A"), (pad, pad))
    alpha = alpha.filter(ImageFilter.GaussianBlur(blur)).point(lambda p: round(p * opacity / 255))
    shadow.putalpha(alpha)
    canvas.alpha_composite(shadow, (x-pad, y-pad+offset))
    canvas.alpha_composite(image, (x, y))


def screen(canvas, name, x, y, width, dark=False):
    image = load_screen(name)
    image = image.resize((width, round(image.height * width / image.width)), Image.Resampling.LANCZOS)
    paste_shadow(canvas, image, x, y, opacity=115 if dark else 45, blur=24, offset=17)


def brand(canvas, x, y, width, variant):
    image = Image.open(OUT / f"kitn-logo-{variant}.png").convert("RGBA")
    image = image.crop(image.getchannel("A").getbbox())
    image = image.resize((width, round(image.height * width / image.width)), Image.Resampling.LANCZOS)
    canvas.alpha_composite(image, (x, y))


def title(canvas, dark=False):
    ImageDraw.Draw(canvas).text(
        (SIZE[0] // 2, 88),
        "Kitten Intelligent Trajectory Navigator",
        font=ImageFont.truetype(str(FONT), 56),
        fill="#e7e9ec" if dark else "#24292f",
        anchor="mm",
    )


def render_banners():
    # 01: White, broad margins, evenly separated floating windows.
    canvas = Image.new("RGBA", SIZE, "#ffffff")
    title(canvas)
    brand(canvas, 145, 535, 255, "black")
    screen(canvas, "ascent", 550, 360, 760)
    screen(canvas, "porkchop", 1400, 250, 820)
    screen(canvas, "docking", 2310, 360, 760)
    screen(canvas, "mission-control-retro", 870, 1100, 500)
    screen(canvas, "remote-control", 1970, 1100, 500)
    canvas.convert("RGB").save(OUT / "kitn-banner-01-white.png", optimize=True)

    # 02: Dark, ordered rows, gold branding and instruments across the top.
    canvas = Image.new("RGBA", SIZE, "#0d1015")
    title(canvas, dark=True)
    brand(canvas, 245, 130, 245, "gold")
    screen(canvas, "mission-control-retro", 1510, 190, 510, dark=True)
    screen(canvas, "remote-control", 2460, 190, 500, dark=True)
    screen(canvas, "ascent", 170, 620, 850, dark=True)
    screen(canvas, "porkchop", 1175, 560, 850, dark=True)
    screen(canvas, "docking", 2180, 620, 850, dark=True)
    canvas.convert("RGB").save(OUT / "kitn-banner-02-dark.png", optimize=True)
    # Borea description images allow at most 2048 pixels per side and 1 MiB.
    canvas.convert("RGB").resize((2048, 1024), Image.Resampling.LANCZOS).save(
        OUT / "kitn-banner-borea.png", optimize=True
    )

    # 03: Charcoal hero; larger Porkchop with small, offset foreground widgets.
    canvas = Image.new("RGBA", SIZE, "#181a1d")
    title(canvas, dark=True)
    brand(canvas, 190, 115, 180, "white")
    screen(canvas, "ascent", 210, 480, 850, dark=True)
    screen(canvas, "docking", 2280, 410, 760, dark=True)
    screen(canvas, "porkchop", 1170, 240, 1000, dark=True)
    screen(canvas, "mission-control-retro", 690, 1130, 555, dark=True)
    screen(canvas, "remote-control", 2010, 1190, 470, dark=True)
    canvas.convert("RGB").save(OUT / "kitn-banner-03-charcoal.png", optimize=True)


def main():
    OUT.mkdir(exist_ok=True, parents=True)
    write_logos()
    render_banners()
    manifest = {
        "dimensions": list(SIZE),
        "label": "Kitten Intelligent Trajectory Navigator",
        "method": "Raster composition of fresh native UI captures; no AI-generated UI.",
        "capture_date": "2026-11-15",
        "screens": ["ascent", "porkchop", "docking", "mission-control-retro", "remote-control"],
        "icon": "KITN/Assets/Icons/cat.svg (Lucide, ISC)",
        "wordmark": "Bahnschrift, weight 600, outlined KITN with 13 px tracking at a 100 px em.",
    }
    (OUT / "artwork.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    for path in OUT.glob("*.png"):
        print(f"{path.name}: {Image.open(path).size}")


if __name__ == "__main__":
    main()
