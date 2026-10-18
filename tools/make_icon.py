"""Build the app and tray icons from the macOS app icon.

Writes src/WhisperShortcut/app.ico (exe, window, tray "ready") and the two tray state icons
recording.ico (red, white dot) and processing.ico (amber, three dots), same rounded-square shape.

Usage: python3 tools/make_icon.py [path/to/AppIcon-1024.png] [preview-dir]
Needs Pillow (pip install pillow). Default source: the 1024 px PNG of the macOS app's AppIcon set.

The macOS icon is a full-bleed blue rounded square. For the small sizes (tray, title bar) the
square is cropped a little so the clipboard and sound waves stay readable at 16 px, and the
corners are re-rounded at the target size.
"""
import os
import sys

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_SRC = os.path.expanduser(
    "~/whisper-shortcut-private/whisper-shortcut/WhisperShortcut/Assets.xcassets/AppIcon.appiconset/image.png")
OUT_DIR = os.path.join(ROOT, "src", "WhisperShortcut")
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48]  # small icon sizes from 100 % to 300 % DPI
RED = (220, 38, 38)
AMBER = (217, 119, 6)
SMALL = 32  # sizes up to this get the tighter crop


def rounded(img: Image.Image, radius: float) -> Image.Image:
    s = img.size[0]
    scale = 8  # supersampled mask for smooth corners
    mask = Image.new("L", (s * scale, s * scale), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, s * scale - 1, s * scale - 1), radius * scale, fill=255)
    mask = mask.resize((s, s), Image.LANCZOS)
    out = img.copy()
    out.putalpha(mask)  # img is opaque here, so the mask is the whole alpha
    return out


def render(src: Image.Image, size: int) -> Image.Image:
    w = src.size[0]
    if size <= SMALL:
        # Crop 7 % per side: the glyph grows, the blue frame stays visible.
        m = round(w * 0.07)
        img = src.crop((m, m, w - m, w - m)).convert("RGB").convert("RGBA")
        img = img.resize((size, size), Image.LANCZOS)
        return rounded(img, size * 0.2)
    return src.resize((size, size), Image.LANCZOS)


def state_icon(size: int, color, glyph: str) -> Image.Image:
    """Tray state icon: colored rounded square with a white glyph, drawn 8x and scaled down."""
    k = 8
    n = size * k
    img = Image.new("RGBA", (n, n), color + (255,))
    d = ImageDraw.Draw(img)
    white = (255, 255, 255, 255)
    if glyph == "dot":
        r = n * 0.25
        d.ellipse((n / 2 - r, n / 2 - r, n / 2 + r, n / 2 + r), fill=white)
    else:  # three dots
        r = n * 0.1
        for cx in (n * 0.22, n * 0.5, n * 0.78):
            d.ellipse((cx - r, n / 2 - r, cx + r, n / 2 + r), fill=white)
    img = img.resize((size, size), Image.LANCZOS)
    return rounded(img, size * 0.2)


def save_ico(name: str, images, preview_dir):
    path = os.path.join(OUT_DIR, name)
    # Pillow writes each size from the largest image unless append_images supplies them.
    # BMP frames (not PNG): readable by every Windows icon API, including System.Drawing.Icon.
    images[-1].save(path, format="ICO", sizes=[im.size for im in images], append_images=images[:-1],
                    bitmap_format="bmp")
    print(f"wrote {path}")
    if preview_dir:
        for im in images:
            im.save(os.path.join(preview_dir, f"{name[:-4]}-{im.size[0]}.png"))


def main() -> None:
    src = Image.open(sys.argv[1] if len(sys.argv) > 1 and sys.argv[1] else DEFAULT_SRC).convert("RGBA")
    preview = sys.argv[2] if len(sys.argv) > 2 else None  # optional folder for PNG previews
    save_ico("app.ico", [render(src, s) for s in SIZES], preview)
    save_ico("recording.ico", [state_icon(s, RED, "dot") for s in TRAY_SIZES], preview)
    save_ico("processing.ico", [state_icon(s, AMBER, "dots") for s in TRAY_SIZES], preview)


if __name__ == "__main__":
    main()
