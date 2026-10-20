"""Draws assets/PartyBridge.ico: two speech bubbles (Discord blue, Xbox green) joined by an amber bridge."""
from pathlib import Path
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

BLUE, GREEN, AMBER, WHITE = (88, 101, 242, 255), (44, 162, 67, 255), (224, 163, 58, 255), (255, 255, 255, 255)

def bubble(x0, y0, x1, y1, color, tail_left):
    d.rounded_rectangle([x0, y0, x1, y1], 110, fill=color)
    if tail_left:
        d.polygon([(x0 + 90, y1 - 20), (x0 + 40, y1 + 120), (x0 + 220, y1 - 20)], fill=color)
    else:
        d.polygon([(x1 - 90, y1 - 20), (x1 - 40, y1 + 120), (x1 - 220, y1 - 20)], fill=color)
    # three "voice" dots
    cy = (y0 + y1) / 2
    cx = (x0 + x1) / 2
    for k in (-1, 0, 1):
        d.ellipse([cx + k * 95 - 34, cy - 34, cx + k * 95 + 34, cy + 34], fill=WHITE)

bubble(40, 120, 470, 480, BLUE, True)
bubble(554, 120, 984, 480, GREEN, False)

# Bridge: a half-circle arch standing on a deck, with hangers.
d.arc([150, 640, 874, 960], 180, 360, fill=AMBER, width=64)
d.rounded_rectangle([90, 770, 934, 840], 34, fill=AMBER)
for x in (300, 420, 604, 724):
    d.rounded_rectangle([x - 16, 670, x + 16, 790], 12, fill=AMBER)

out = Path(__file__).resolve().parent.parent / "assets" / "PartyBridge.ico"
img.save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save(out.with_suffix(".png"))
print("wrote", out)
