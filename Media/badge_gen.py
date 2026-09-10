#!/usr/bin/env python3
"""Generates the suite badge family (300x100, CE-badge geometry).

Base: full-width bar, 5px ring knockout, emblem circle with CE's rifle
(remixed from Badge_CE_compatible.png, CC BY-NC-SA, CE team) over a drawn
sidearm pistol (SS ships no licensed art). Module variants add an accent
ring + colored subtitle.

Run from Media/:  python3 badge_gen.py
"""
import collections
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
CE_BADGE = os.path.join(HERE, "Badge_CE_compatible.png")
FONT = "/usr/share/fonts/dejavu-sans-fonts/DejaVuSansCondensed-Bold.ttf"
S = 4
BLACK = (0, 0, 0, 255)
WHITE = (255, 255, 255, 255)


def extract_rifle():
    """Rifle glyph from CE's badge, vector-sharp: rasterize the SVG at 8x via
    cairosvg (falls back to the 300px PNG if cairosvg is unavailable)."""
    try:
        import io
        import cairosvg
        buf = io.BytesIO()
        cairosvg.svg2png(url=os.path.join(HERE, "Badge_CE_compatible.svg"),
                         write_to=buf, scale=8)
        buf.seek(0)
        src = Image.open(buf).convert("RGBA")
        Z = 8
    except ImportError:
        src = Image.open(CE_BADGE).convert("RGBA")
        Z = 1
    px = src.load()
    pts = [(x, y) for x in range(105 * Z) for y in range(100 * Z)
           if px[x, y][0] > 200 and px[x, y][3] > 200]
    ptset = set(pts)
    seen = set()
    clusters = []
    for p in pts:
        if p in seen:
            continue
        q = collections.deque([p])
        comp = []
        seen.add(p)
        while q:
            c = q.popleft()
            comp.append(c)
            x, y = c
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    n = (x + dx, y + dy)
                    if n in ptset and n not in seen:
                        seen.add(n)
                        q.append(n)
        clusters.append(comp)

    # The rifle is the big glyph in the circle's lower-left quadrant (the
    # largest cluster overall is the sombrero skull).
    def is_rifle(comp):
        xs = [p[0] for p in comp]
        ys = [p[1] for p in comp]
        cx = (min(xs) + max(xs)) / 2
        cy = (min(ys) + max(ys)) / 2
        return cx < 50 * Z and cy > 40 * Z

    comp = max((c for c in clusters if is_rifle(c)), key=len)
    xs = [p[0] for p in comp]
    ys = [p[1] for p in comp]
    x0, y0 = min(xs), min(ys)
    m = Image.new("L", (max(xs) - x0 + 1, max(ys) - y0 + 1), 0)
    mp = m.load()
    for x, y in comp:
        mp[x - x0, y - y0] = 255
    if Z == 1:
        m = m.resize((m.width * 8, m.height * 8), Image.NEAREST)
    m = m.rotate(45, expand=True, resample=Image.BICUBIC)
    m = m.transpose(Image.FLIP_LEFT_RIGHT).point(lambda v: 255 if v > 110 else 0)
    return m.crop(m.getbbox())


def draw_pistol(d, ox, oy, s, flip=False):
    def P(pts):
        out = []
        for x, y in pts:
            if flip:
                x = 60 - x
            out.append((ox + x * s, oy + y * s))
        return out
    body = [(0, 4), (3, 4), (3, 2), (6, 2), (6, 4), (50, 4), (50, 2), (53, 2),
            (53, 4), (58, 4), (58, 13), (44, 13), (44, 17), (33, 17), (33, 27),
            (24, 27), (24, 19), (20, 19), (13, 40), (1, 40), (5, 17), (0, 15)]
    d.polygon(P(body), fill=WHITE)
    d.polygon(P([(27, 19), (31, 19), (31, 24), (27, 24)]), fill=BLACK)
    d.polygon(P([(25.5, 19), (27.5, 19), (27.5, 23.5), (25.5, 22.5)]), fill=WHITE)


def render(path, subtitle, accent=None, rifle=None):
    W, H = 300 * S, 100 * S
    bar = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    db = ImageDraw.Draw(bar)
    db.rectangle([0, 25 * S, 300 * S, 74 * S], fill=BLACK)
    hole = Image.new("L", (W, H), 0)
    dh = ImageDraw.Draw(hole)
    cx, cy, r, gap = 50 * S, 50 * S, 50 * S, 5 * S
    dh.ellipse([cx - (r + gap), cy - (r + gap), cx + (r + gap), cy + (r + gap)], fill=255)
    dh.rectangle([0, 0, 5 * S, H], fill=255)
    bar.putalpha(Image.composite(Image.new("L", (W, H), 0), bar.getchannel("A"), hole))

    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    img.alpha_composite(bar)
    d = ImageDraw.Draw(img)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=BLACK)
    if accent:
        ring = 3 * S
        d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=accent, width=ring)

    def paste_glyph(m, gx, gy, target_w):
        sc = target_w / m.width
        g = m.resize((int(m.width * sc), int(m.height * sc)), Image.LANCZOS)
        img.paste(WHITE, (int(gx - g.width / 2), int(gy - g.height / 2)), g)

    paste_glyph(rifle, 50 * S, 35 * S, 62 * S)
    draw_pistol(d, 31 * S, 56 * S, 0.62 * S, flip=True)

    f1 = ImageFont.truetype(FONT, 15 * S)
    f2 = ImageFont.truetype(FONT, 10 * S)
    CX = 202 * S
    t1 = "CE + SIMPLE SIDEARMS"
    w1 = d.textlength(t1, font=f1)
    d.text((CX - w1 / 2, 32 * S), t1, font=f1, fill=WHITE)
    K = 1.6 * S
    w2 = sum(d.textlength(c, font=f2) + K for c in subtitle) - K
    x = CX - w2 / 2
    for ch in subtitle:
        d.text((x, 55 * S), ch, font=f2, fill=accent or WHITE)
        x += d.textlength(ch, font=f2) + K

    img.resize((300, 100), Image.LANCZOS).save(path)
    print("wrote", path)


def render_preview(path, subtitle, accent, rifle):
    """512x512 Workshop preview: big emblem over stacked title lines."""
    P = 4
    W = H = 512 * P
    img = Image.new("RGBA", (W, H), (12, 12, 12, 255))
    d = ImageDraw.Draw(img)
    cx, cy, r = 256 * P, 190 * P, 140 * P
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=BLACK, outline=accent, width=8 * P)

    def paste_glyph(m, gx, gy, target_w):
        sc = target_w / m.width
        g = m.resize((int(m.width * sc), int(m.height * sc)), Image.LANCZOS)
        img.paste(WHITE, (int(gx - g.width / 2), int(gy - g.height / 2)), g)

    paste_glyph(rifle, cx, 150 * P, 176 * P)
    dd = ImageDraw.Draw(img)
    draw_pistol(dd, (256 - 54) * P, 208 * P, 1.75 * P, flip=True)

    f = ImageFont.truetype(FONT, 34 * P)
    plh = sum(f.getmetrics())
    ytop = 358 * P
    for i, (text, color) in enumerate([
        ("COMBAT EXTENDED", WHITE),
        ("+ SIMPLE SIDEARMS", WHITE),
        (subtitle, accent),
    ]):
        w = dd.textlength(text, font=f)
        dd.text(((W - w) / 2, ytop + i * plh), text, font=f, fill=color)

    img.resize((512, 512), Image.LANCZOS).save(path)
    print("wrote", path)


if __name__ == "__main__":
    rifle = extract_rifle()
    render(os.path.join(HERE, "Badge_Suite.png"), "COMPATIBILITY SUITE", None, rifle)
    render(os.path.join(HERE, "Badge_Patch.png"), "COMPATIBILITY PATCH", (109, 143, 60, 255), rifle)
    render(os.path.join(HERE, "Badge_Loadouts.png"), "LOADOUTS MODULE", (217, 154, 43, 255), rifle)
    render(os.path.join(HERE, "Badge_Tactics.png"), "TACTICS MODULE", (176, 65, 62, 255), rifle)
    render_preview(os.path.join(HERE, "..", "About", "Preview.png"),
                   "COMPATIBILITY PATCH", (109, 143, 60, 255), rifle)
    # Distribute the full badge set to sibling repos so their READMEs can
    # cross-link with relative paths (personal tooling — skipped when absent).
    import shutil
    badge_set = ["Badge_Suite.png", "Badge_Patch.png", "Badge_Loadouts.png", "Badge_Tactics.png"]
    for sibling in ("~/Projects/CombatExtended-SimpleSidearms-Compatibility-Loadouts",
                    "~/Projects/CombatExtended-SimpleSidearms-Compatibility-Tactics"):
        media = os.path.expanduser(sibling + "/Media")
        if os.path.isdir(media):
            for name in badge_set:
                shutil.copy(os.path.join(HERE, name), os.path.join(media, name))
            print("distributed badges ->", media)

    loadouts_about = os.path.expanduser("~/Projects/CombatExtended-SimpleSidearms-Compatibility-Loadouts/About")
    if os.path.isdir(loadouts_about):
        render_preview(os.path.join(loadouts_about, "Preview.png"),
                       "LOADOUTS MODULE", (217, 154, 43, 255), rifle)
    tactics_about = os.path.expanduser(
        "~/Projects/CombatExtended-SimpleSidearms-Compatibility-Tactics/About")
    if os.path.isdir(tactics_about):
        render_preview(os.path.join(tactics_about, "Preview.png"),
                       "TACTICS MODULE", (176, 65, 62, 255), rifle)
