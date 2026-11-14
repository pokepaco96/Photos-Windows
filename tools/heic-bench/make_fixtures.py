"""Generates synthetic, licence-free HEIC fixtures for the WIC/HEIC benchmark (no personal photos).

usage: python make_fixtures.py OUT_DIR
Writes 12, 24 and 48 MP photos, one with EXIF orientation 6 and GPS, one 10-bit, and a truncated file,
plus manifest.json with size, dimensions and SHA-256 of each file (hashes depend on the encoder version).
"""
import hashlib, json, os, random, sys, zlib
from PIL import Image, ImageDraw, ImageFilter
import pillow_heif

pillow_heif.register_heif_opener()
out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def picture(w, h, seed):
    rnd = random.Random(seed)
    small = Image.new("RGB", (w // 8, h // 8))
    d = ImageDraw.Draw(small)
    for y in range(h // 8):  # sky-like vertical gradient
        t = y / max(1, h // 8 - 1)
        d.line([(0, y), (w // 8, y)], fill=(int(90 + 120 * t), int(140 + 60 * t), int(220 - 80 * t)))
    for _ in range(60):
        x, y, r = rnd.randrange(w // 8), rnd.randrange(h // 8), rnd.randrange(8, max(9, w // 40))
        d.ellipse([x - r, y - r, x + r, y + r], fill=(rnd.randrange(256), rnd.randrange(256), rnd.randrange(256)))
    img = small.resize((w, h), Image.BICUBIC).filter(ImageFilter.GaussianBlur(2))
    noise = Image.effect_noise((w, h), 18).convert("RGB")  # sensor-like grain so the codec has real work
    return Image.blend(img, noise, 0.12)


def exif(orientation=1, gps=False):
    e = Image.Exif()
    e[0x0112] = orientation
    e[0x010F] = "QA Labs"
    e[0x0110] = "QA Cam HEIC"
    if gps:
        e[0x8825] = {1: "N", 2: (40.0, 25.0, 0.0), 3: "W", 4: (3.0, 42.0, 0.0)}
    return e.tobytes()


files = []
specs = [
    ("heic_12mp.heic", 4032, 3024, 1, False, 8),
    ("heic_12mp_rot6_gps.heic", 4032, 3024, 6, True, 8),
    ("heic_24mp.heic", 6000, 4000, 1, False, 8),
    ("heic_48mp.heic", 8064, 6048, 1, False, 8),
    ("heic_12mp_10bit.heic", 4032, 3024, 1, False, 10),
]
for name, w, h, orient, gps, bits in specs:
    path = os.path.join(out, name)
    img = picture(w, h, zlib.crc32(name.encode()))
    if bits == 10:
        heif = pillow_heif.from_pillow(img)
        heif.save(path, quality=80, save_to_12bit=False, bit_depth=10, exif=exif(orient, gps)) if hasattr(heif, "save") else None
        if not os.path.exists(path):
            img.save(path, format="HEIF", quality=80, exif=exif(orient, gps))
    else:
        img.save(path, format="HEIF", quality=80, exif=exif(orient, gps))
    files.append(path)

trunc = os.path.join(out, "heic_truncated.heic")
data = open(files[0], "rb").read()
open(trunc, "wb").write(data[: len(data) * 2 // 5])
files.append(trunc)

manifest = []
for p in files:
    b = open(p, "rb").read()
    entry = {"file": os.path.basename(p), "bytes": len(b), "sha256": hashlib.sha256(b).hexdigest(), "brand": b[8:12].decode("latin1")}
    try:
        with Image.open(p) as im:
            entry["width"], entry["height"], entry["mode"] = im.width, im.height, im.mode
    except Exception as ex:  # the truncated file
        entry["error"] = type(ex).__name__
    manifest.append(entry)
json.dump({"generator": "pillow-heif " + pillow_heif.__version__, "files": manifest}, open(os.path.join(out, "manifest.json"), "w"), indent=2)
print(json.dumps(manifest, indent=2))
