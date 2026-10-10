"""Builds the Crowsnest media kit: logo SVGs and PNGs, the app icon (.ico) and the social image.

Run from the repository root:

    python -m venv .venv-media
    .venv-media\\Scripts\\python -m pip install -r media\\tools\\requirements.txt
    .venv-media\\Scripts\\python media\\tools\\build_media.py

The mark is drawn here, in code, so every file comes from one drawing. The wordmark is DM Sans
(media/fonts) turned into outlines, so no file needs the font installed to look right. PNGs are
rendered by headless Chrome or Edge, whichever is installed.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from PIL import Image

MEDIA = Path(__file__).resolve().parent.parent
FONT = MEDIA / "fonts" / "DMSans-Variable.ttf"

# The panel's own palette (firmware components/crowsnest_ui/ui_view.h).
BLACK = "#000000"
WHITE = "#F2F2F2"
GREEN = "#35E05B"
AMBER = "#FFB000"
BLUE = "#35B7FF"
GREY = "#8A8A8A"
INK = "#111111"  # the mark's dark colour on light backgrounds
PAPER = "#FFFFFF"

WORD = "CROWSNEST"
WORD_WEIGHT = 600
WORD_TRACKING = 0.12  # of the font size

# ---------------------------------------------------------------------------------------------
# The mark: a crow's nest on its mast, in the panel's rounded frame.
#
# Drawn on a 32 x 32 grid: one cell is a pixel at 32 px and half a pixel at 16 px. The rules
# (README, "Construction"):
#   - two weights: the frame 2 cells, the structure (mast, rim) 3, so the nest reads first
#   - one gap: 2 cells, 1 px at 16 px (flag to mast, flag to rim, rim to basket, frame to contents)
#   - only the frame is rounded, concentrically (tile 6, frame outer 4, inner 2); the rest is square
#   - the mast sits half a cell left of centre, so mast and flag together are centred
#   - the flag hangs 2 cells below the mast top; the basket's sides taper 1 cell in 2
# ---------------------------------------------------------------------------------------------

GRID = 32
MARK_SHAPES = """
<rect x="14" y="5" width="3" height="23"/>
<rect x="6" y="13" width="19" height="3"/>
<path d="M8 18 L23 18 L21 22 L10 22 Z"/>"""
MARK_FLAG = "M19 7 L26 9 L19 11 Z"


def mark_group(fg: str, accent: str, tile: str | None) -> str:
    """The mark as an SVG fragment in a 100 x 100 box."""
    tile_svg = f'<rect width="32" height="32" rx="6" fill="{tile}"/>' if tile else ""
    return f"""<g transform="scale({100 / GRID:g})">{tile_svg}
<rect x="3" y="3" width="26" height="26" rx="3" fill="none" stroke="{fg}" stroke-width="2"/>
<g fill="{fg}">{MARK_SHAPES}</g>
<path d="{MARK_FLAG}" fill="{accent}"/>
</g>"""


def construction_svg() -> str:
    """The mark on its grid, with the content box and centre line, for the README."""
    lines = "".join(
        f'<path d="M{i} 0 V32 M0 {i} H32" stroke="{BLUE}" stroke-width="0.06" opacity="0.6"/>' for i in range(33)
    )
    guides = (
        f'<rect x="6" y="5" width="20" height="23" fill="none" stroke="{AMBER}" stroke-width="0.12" stroke-dasharray="0.5 0.4"/>'
        f'<path d="M16 0 V32" stroke="{AMBER}" stroke-width="0.12" stroke-dasharray="0.5 0.4"/>'
    )
    body = f"""<rect width="100" height="100" fill="#1A1A1A"/>
<g opacity="0.85">{mark_group(WHITE, GREEN, BLACK)}</g>
<g transform="scale({100 / GRID:g})">{lines}{guides}</g>"""
    return svg_doc(100, 100, body, "Crowsnest mark on its 32 by 32 construction grid")


def svg_doc(width: float, height: float, body: str, title: str, px: tuple[int, int] | None = None) -> str:
    size = f' width="{px[0]}" height="{px[1]}"' if px else ""
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width:g} {height:g}"{size} role="img" aria-label="{title}">
<title>{title}</title>
{body}
</svg>
"""


# ---------------------------------------------------------------------------------------------
# The wordmark: DM Sans outlines.
# ---------------------------------------------------------------------------------------------

def load_font(weight: int, opsz: float) -> TTFont:
    font = TTFont(FONT)
    return instantiateVariableFont(font, {"wght": weight, "opsz": opsz})


def text_path(font: TTFont, text: str, size: float, tracking: float, x: float, baseline: float) -> tuple[str, float]:
    """SVG path data for text set at (x, baseline); returns the path and its advance width."""
    upem = font["head"].unitsPerEm
    scale = size / upem
    cmap = font.getBestCmap()
    glyphs = font.getGlyphSet()
    hmtx = font["hmtx"]
    pen = SVGPathPen(glyphs)
    cursor = 0.0
    names = [cmap[ord(ch)] for ch in text]
    for i, name in enumerate(names):
        tpen = TransformPen(pen, (scale, 0, 0, -scale, x + cursor, baseline))
        glyphs[name].draw(tpen)
        cursor += hmtx[name][0] * scale
        if i < len(names) - 1:
            cursor += tracking * size
    return pen.getCommands(), cursor


def cap_height(font: TTFont) -> float:
    return font["OS/2"].sCapHeight / font["head"].unitsPerEm


# ---------------------------------------------------------------------------------------------
# Layouts
# ---------------------------------------------------------------------------------------------

def mark_svg(fg: str, accent: str, tile: str | None, title: str) -> str:
    return svg_doc(100, 100, mark_group(fg, accent, tile), title)


def horizontal_svg(font: TTFont, fg: str, accent: str, tile: str | None, text: str, title: str) -> str:
    # Mark 100 tall; caps 34 tall, centred on it; 24 between.
    caps = 34
    size = caps / cap_height(font)
    x0 = 100 + 24
    d, advance = text_path(font, WORD, size, WORD_TRACKING, x0, 50 + caps / 2)
    width = x0 + advance
    body = mark_group(fg, accent, tile) + f'\n<path d="{d}" fill="{text}"/>'
    return svg_doc(round(width, 2), 100, body, title)


def stacked_svg(font: TTFont, fg: str, accent: str, tile: str | None, text: str, title: str) -> str:
    # Mark 100 wide; caps 15 tall, 20 below it, centred under it.
    caps = 15
    size = caps / cap_height(font)
    _, advance = text_path(font, WORD, size, WORD_TRACKING, 0, 0)
    width = max(100, advance)
    mx = (width - 100) / 2
    baseline = 100 + 20 + caps
    d, _ = text_path(font, WORD, size, WORD_TRACKING, (width - advance) / 2, baseline)
    body = f'<g transform="translate({mx:.2f} 0)">{mark_group(fg, accent, tile)}</g>\n<path d="{d}" fill="{text}"/>'
    return svg_doc(round(width, 2), baseline, body, title)


def wordmark_svg(font: TTFont, colour: str, title: str) -> str:
    caps = 70
    size = caps / cap_height(font)
    d, advance = text_path(font, WORD, size, WORD_TRACKING, 0, caps)
    return svg_doc(round(advance, 2), caps, f'<path d="{d}" fill="{colour}"/>', title)


def social_svg(font: TTFont, tag_font: TTFont) -> str:
    # 1280 x 640 (the GitHub social preview size): lockup centred, tagline under it.
    w, h = 1280, 640
    mark = 220
    caps = mark * 0.34
    size = caps / cap_height(font)
    _, advance = text_path(font, WORD, size, WORD_TRACKING, 0, 0)
    gap = mark * 0.24
    total = mark + gap + advance
    x = (w - total) / 2
    y = 170
    d, _ = text_path(font, WORD, size, WORD_TRACKING, x + mark + gap, y + mark / 2 + caps / 2)
    tagline = "Cockpit panels for Microsoft Flight Simulator"
    tsize = 34
    _, tadv = text_path(tag_font, tagline, tsize, 0, 0, 0)
    td, _ = text_path(tag_font, tagline, tsize, 0, (w - tadv) / 2, y + mark + 110)
    s = mark / 100
    body = f"""<rect width="{w}" height="{h}" fill="{BLACK}"/>
<g transform="translate({x:.2f} {y}) scale({s:g})">{mark_group(WHITE, GREEN, None)}</g>
<path d="{d}" fill="{WHITE}"/>
<path d="{td}" fill="{GREY}"/>"""
    return svg_doc(w, h, body, "Crowsnest: cockpit panels for Microsoft Flight Simulator")


# ---------------------------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------------------------

def find_browser() -> str:
    for candidate in (
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        shutil.which("chrome"),
        shutil.which("msedge"),
    ):
        if candidate and Path(candidate).exists():
            return candidate
    sys.exit("Needs Chrome or Edge to render PNGs.")


def render_png(browser: str, svg: str, width: int, height: int) -> Image.Image:
    """Renders an SVG at exactly width x height pixels, transparent where the SVG is."""
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        page = tmp_path / "page.html"
        out = tmp_path / "out.png"
        page.write_text(
            "<!doctype html><html><head><style>html,body{margin:0;background:transparent;overflow:hidden}"
            f"svg{{display:block;width:{width}px;height:{height}px}}</style></head><body>{svg}</body></html>",
            encoding="utf-8",
        )
        subprocess.run(
            [
                browser,
                "--headless=new",
                "--disable-gpu",
                "--hide-scrollbars",
                "--force-device-scale-factor=1",
                "--default-background-color=00000000",
                f"--user-data-dir={tmp_path / 'profile'}",
                f"--window-size={width},{height}",
                f"--screenshot={out}",
                page.as_uri(),
            ],
            check=True,
            capture_output=True,
            timeout=60,
        )
        with Image.open(out) as img:
            img.load()
            result = img.convert("RGBA")
    if result.size != (width, height):
        result = result.crop((0, 0, width, height))
    return result


def svg_size(svg: str) -> tuple[float, float]:
    head = svg.split('viewBox="', 1)[1].split('"', 1)[0].split()
    return float(head[2]), float(head[3])


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")
    print(f"  {path.relative_to(MEDIA)}")


def save_png(path: Path, img: Image.Image) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, optimize=True)
    print(f"  {path.relative_to(MEDIA)}")


def main() -> None:
    word = load_font(WORD_WEIGHT, 24)
    tag = load_font(400, 14)
    browser = find_browser()

    logos = MEDIA / "logo"
    marks = {
        # name: (fg, accent, tile, alt text)
        "mark": (WHITE, GREEN, BLACK, "Crowsnest logo"),
        "mark-light": (INK, GREEN, PAPER, "Crowsnest logo"),
        "mark-white": (WHITE, WHITE, None, "Crowsnest logo"),
        "mark-black": (INK, INK, None, "Crowsnest logo"),
    }
    svgs: dict[str, str] = {}
    for name, (fg, accent, tile, alt) in marks.items():
        svgs[f"crowsnest-{name}"] = mark_svg(fg, accent, tile, alt)
    svgs["crowsnest-horizontal"] = horizontal_svg(word, WHITE, GREEN, BLACK, WHITE, "Crowsnest")
    svgs["crowsnest-horizontal-light"] = horizontal_svg(word, INK, GREEN, PAPER, INK, "Crowsnest")
    svgs["crowsnest-stacked"] = stacked_svg(word, WHITE, GREEN, BLACK, WHITE, "Crowsnest")
    svgs["crowsnest-stacked-light"] = stacked_svg(word, INK, GREEN, PAPER, INK, "Crowsnest")
    svgs["crowsnest-wordmark-white"] = wordmark_svg(word, WHITE, "Crowsnest")
    svgs["crowsnest-wordmark-black"] = wordmark_svg(word, INK, "Crowsnest")

    print("Logos")
    for name, svg in svgs.items():
        write(logos / "svg" / f"{name}.svg", svg)
        w, h = svg_size(svg)
        # PNGs 512 px tall (marks: 512 square), enough for slides and web pages.
        px_h = 512
        px_w = round(w / h * px_h)
        save_png(logos / "png" / f"{name}.png", render_png(browser, svg, px_w, px_h))

    construction = construction_svg()
    write(logos / "svg" / "crowsnest-construction.svg", construction)
    save_png(logos / "png" / "crowsnest-construction.png", render_png(browser, construction, 512, 512))

    print("App icon")
    icons = MEDIA / "icon"
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames: list[Image.Image] = []
    for px in sizes:
        img = render_png(browser, svgs["crowsnest-mark"], px, px)
        frames.append(img)
        save_png(icons / "png" / f"crowsnest-{px}.png", img)
    largest = frames[-1]
    largest.save(icons / "crowsnest.ico", format="ICO", sizes=[(p, p) for p in sizes], append_images=frames[:-1])
    print(f"  {(icons / 'crowsnest.ico').relative_to(MEDIA)}")

    print("Swatches")
    palette = {"black": BLACK, "white": WHITE, "green": GREEN, "amber": AMBER, "blue": BLUE, "grey": GREY, "ink": INK}
    for name, colour in palette.items():
        swatch = svg_doc(24, 24, f'<rect width="24" height="24" rx="4" fill="{colour}" stroke="{GREY}" stroke-width="1"/>', name)
        write(MEDIA / "colour" / f"{name}.svg", swatch)

    print("Social")
    social = social_svg(word, tag)
    write(MEDIA / "social" / "crowsnest-social.svg", social)
    save_png(MEDIA / "social" / "crowsnest-social.png", render_png(browser, social, 1280, 640))


if __name__ == "__main__":
    main()
