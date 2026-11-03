"""Draws the Pocket Agent icon (a pocket with a spark) and writes a multi-size .ico and a PNG."""
import sys
from PIL import Image, ImageDraw, ImageFilter

S = 1024

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def gradient(size, c1, c2):
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x * 0.55 + y * 0.45) / size
            px[x, y] = lerp(c1, c2, min(1, max(0, t)))
    return img

def star(draw, cx, cy, r, fill, waist=0.28):
    pts = []
    for i in range(8):
        import math
        ang = math.pi / 4 * i - math.pi / 2
        rad = r if i % 2 == 0 else r * waist
        pts.append((cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
    draw.polygon(pts, fill=fill)

def icon():
    base = gradient(S // 4, (124, 108, 255), (34, 211, 238)).resize((S, S), Image.BICUBIC)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((40, 40, S - 40, S - 40), radius=240, fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(base, (0, 0), mask)
    # soft top highlight
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(hl).ellipse((-200, -520, S + 200, 430), fill=(255, 255, 255, 38))
    hl_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(hl_mask).rounded_rectangle((40, 40, S - 40, S - 40), radius=240, fill=255)
    out = Image.composite(Image.alpha_composite(out, hl), out, hl_mask)
    d = ImageDraw.Draw(out)
    # pocket: rounded U with a flap line
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((262, 372, 762, 822), radius=150, fill=(20, 16, 60, 90))
    shadow = shadow.filter(ImageFilter.GaussianBlur(28))
    out = Image.alpha_composite(out, shadow)
    d = ImageDraw.Draw(out)
    d.rounded_rectangle((262, 340, 762, 790), radius=150, fill=(255, 255, 255, 255))
    d.rectangle((262, 340, 762, 470), fill=(255, 255, 255, 255))
    d.rounded_rectangle((242, 300, 782, 400), radius=44, fill=(255, 255, 255, 255))
    d.line((300, 452, 724, 452), fill=(124, 108, 255, 255), width=18)
    # spark inside the pocket
    star(d, 512, 615, 132, (99, 88, 245, 255))
    star(d, 512, 615, 54, (34, 211, 238, 255), waist=0.3)
    # small spark above
    star(d, 770, 218, 78, (255, 255, 255, 255))
    return out

def main(ico_path, png_path):
    img = icon()
    img.save(png_path)
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (40, 40), (32, 32), (24, 24), (20, 20), (16, 16)]
    img.save(ico_path, sizes=sizes)

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
