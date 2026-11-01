"""Cuts the Untrail logo out of brand/logos.webp and builds the app's image assets.

    python tools/make_assets.py

Writes src/assets/*.png, src/assets/untrail.ico and brand/*.png.
The source art is on a white background; each pixel is "un-mixed" from white so edges keep
their anti-aliasing as real transparency, then snapped to the exact brand colours.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "brand" / "logos.webp"
ASSETS = ROOT / "src" / "assets"
BRAND = ROOT / "brand"

ORANGE = (0xFF, 0x4A, 0x00)
INK = (0x0B, 0x0F, 0x17)
WHITE = (0xFF, 0xFF, 0xFF)
BG = 254          # the source's white
FG_MIN = 6        # darkest channel of the art (orange's blue / ink's red)

LOCKUP_BOX = (159, 63, 1219, 357)   # mark + "Untrail." in the big top-left lockup
MARK_BOX = (159, 63, 420, 357)


def unmix(img, text_colour):
    """White-background art -> RGBA with exact brand colours. text_colour replaces the ink."""
    img = img.convert("RGB")
    out = Image.new("RGBA", img.size)
    src, dst = img.load(), out.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b = src[x, y]
            a = max(0.0, min(1.0, (BG - min(r, g, b)) / (BG - FG_MIN)))
            if a < 0.02:
                dst[x, y] = (0, 0, 0, 0)
                continue
            # colour before it was blended with white
            ur = (r - BG * (1 - a)) / a
            ub = (b - BG * (1 - a)) / a
            colour = ORANGE if ur - ub > 110 else text_colour
            dst[x, y] = colour + (round(a * 255),)
    return out


def trim(img, pad=4):
    box = img.getbbox()
    img = img.crop(box)
    canvas = Image.new("RGBA", (img.width + pad * 2, img.height + pad * 2))
    canvas.paste(img, (pad, pad))
    return canvas


def app_icon(mark, size, dark=True):
    """Rounded square like the brand's app icon, mark at ~68% of the height."""
    s = 4  # supersample
    big = size * s
    icon = Image.new("RGBA", (big, big))
    d = ImageDraw.Draw(icon)
    d.rounded_rectangle((0, 0, big - 1, big - 1), radius=round(big * 0.22), fill=(INK if dark else WHITE) + (255,))
    h = round(big * (0.70 if size > 24 else 0.78))
    w = round(mark.width * h / mark.height)
    m = mark.resize((w, h), Image.LANCZOS)
    icon.alpha_composite(m, ((big - w) // 2, (big - h) // 2 + round(big * 0.01)))
    return icon.resize((size, size), Image.LANCZOS)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    src = Image.open(SRC)

    mark = trim(unmix(src.crop(MARK_BOX), INK))
    logo_light = trim(unmix(src.crop(LOCKUP_BOX), INK))
    logo_dark = trim(unmix(src.crop(LOCKUP_BOX), WHITE))

    mark.save(ASSETS / "mark.png")
    logo_light.save(ASSETS / "logo-light.png")
    logo_dark.save(ASSETS / "logo-dark.png")
    logo_light.save(BRAND / "logo-light.png")
    logo_dark.save(BRAND / "logo-dark.png")

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    icons = [app_icon(mark, s) for s in sizes]
    icons[-1].save(ASSETS / "untrail.ico", sizes=[(s, s) for s in sizes], append_images=icons[:-1])
    icons[-1].save(BRAND / "icon-256.png")
    for name, im in (("mark", mark), ("logo-light", logo_light), ("logo-dark", logo_dark)):
        print(f"{name}: {im.size}")
    print("icon sizes:", sizes)


if __name__ == "__main__":
    main()
