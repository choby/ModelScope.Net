"""Derive bounded OCR inputs from fixed-revision model-card assets."""
import hashlib
import json
from pathlib import Path
import sys
from PIL import Image


root = Path(sys.argv[1]).resolve()
output = root / "artifacts" / "ocr" / "inputs"
output.mkdir(parents=True, exist_ok=True)
sources = {
    "recognition": root / "artifacts/ocr/recognition-snapshot/resources/rec_result_visu.jpg",
    "detection": root / "artifacts/ocr/recognition-snapshot/resources/rec_result_measure.png",
}
with Image.open(sources["recognition"]) as image:
    image.convert("RGB").crop((0, 0, image.width, 110)).save(output / "recognition.png", format="PNG")
with Image.open(sources["detection"]) as image:
    image.convert("RGB").save(output / "detection.png", format="PNG")
report = {"scope": "deterministic crop/PNG conversion of fixed model-card assets; not independent ground truth", "files": []}
for name in ("recognition", "detection"):
    path = output / f"{name}.png"
    with Image.open(path) as image:
        report["files"].append({"id": name, "path": path.name, "width": image.width, "height": image.height,
                                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                                "sourceSha256": hashlib.sha256(sources[name].read_bytes()).hexdigest()})
(output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
