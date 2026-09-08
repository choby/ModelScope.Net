"""Decode two PNGs and compare RGB pixels; report to stdout, fail on mismatch."""
import hashlib
import json
import sys
from PIL import Image


def read_pixels(path):
    with Image.open(path) as image:
        if image.format != "PNG" or image.width * image.height > 4 * 1024 * 1024:
            raise ValueError("unexpected image format or dimensions")
        rgb = image.convert("RGB")
        return rgb.size, rgb.tobytes()


if __name__ == "__main__":
    first_size, first = read_pixels(sys.argv[1])
    second_size, second = read_pixels(sys.argv[2])
    matched = first_size == second_size and first == second
    report = {"passed": matched, "scope": "exact decoded RGB pixel comparison only",
              "referenceSize": first_size, "actualSize": second_size,
              "referenceRgbSha256": hashlib.sha256(first).hexdigest(),
              "actualRgbSha256": hashlib.sha256(second).hexdigest(),
              "differentChannels": sum(a != b for a, b in zip(first, second)) if first_size == second_size else None}
    rendered = json.dumps(report, indent=2)
    print(rendered)
    if len(sys.argv) == 4:
        with open(sys.argv[3], "x", encoding="utf-8") as stream:
            stream.write(rendered + "\n")
    raise SystemExit(0 if matched else 1)
