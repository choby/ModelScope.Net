"""Generate fixed local OCR outputs through the original ModelScope pipelines."""
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import uuid
from PIL import Image


root = Path(sys.argv[1]).resolve()
output = root / "artifacts" / "ocr" / "python-runs" / uuid.uuid4().hex
output.mkdir(parents=True)
os.environ.update(MODELSCOPE_CACHE=str(output / "cache"), HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
print(output, flush=True)
models = {
    "recognition": ("ocr-recognition", root / "artifacts/ocr/recognition-snapshot", "6346468feced662993f8a79f7f62004dc534cca6"),
    "detection": ("ocr-detection", root / "artifacts/ocr/detection-snapshot", "3a6b98fc046f99e8ec97d4ed25f478909a123253"),
}
report = {"schemaVersion": 1, "status": "failed", "scope": "original Python OCR outputs on derived model-card inputs", "results": {}}
started = time.monotonic()
try:
    from modelscope.pipelines import pipeline
    for name, (task, model_path, revision) in models.items():
        manifest_path = model_path / ".modelscope-net-manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest["resolvedRevision"] != revision:
            raise ValueError("snapshot revision mismatch")
        for item in manifest["files"]:
            path = model_path / item["path"]
            if path.stat().st_size != item["size"] or hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]:
                raise ValueError("snapshot file mismatch")
        image_path = root / "artifacts/ocr/inputs" / f"{name}.png"
        with Image.open(image_path) as source:
            image = source.convert("RGB")
        model = pipeline(task=task, model=str(model_path), device="cpu", trust_remote_code=False)
        raw = model(image)
        value = raw["text"] if name == "recognition" else raw["polygons"].tolist()
        result_path = output / f"{name}.json"
        result_path.write_text(json.dumps({"task": task, "output": {"text" if name == "recognition" else "polygons": value}}, ensure_ascii=False, indent=2), encoding="utf-8")
        report["results"][name] = {"task": task, "revision": revision, "inputSha256": hashlib.sha256(image_path.read_bytes()).hexdigest(),
                                    "manifestSha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest(), "outputSha256": hashlib.sha256(result_path.read_bytes()).hexdigest(),
                                    "textLength": len(value) if name == "recognition" else None, "polygonCount": len(value) if name == "detection" else None}
    report["status"] = "passed"
finally:
    report["elapsedSeconds"] = time.monotonic() - started
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
