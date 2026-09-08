"""Original ModelScope SD1.5 smoke evidence, not a .NET parity certification."""
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import uuid

from verify_sd15_weights import verify

root = Path(__file__).resolve().parents[1]
plan = json.loads((root / "tests" / "compatibility" / "image-generation-plan.json").read_text(encoding="utf-8"))
sample = plan["samples"][int(sys.argv[1]) if len(sys.argv) > 1 else 0]
output = root / "artifacts" / "image-generation" / "python-runs" / uuid.uuid4().hex
output.mkdir(parents=True)
os.environ["MODELSCOPE_CACHE"] = str(output / "cache")
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
print(str(output), flush=True)
report = {"status": "failed", "scope": "original Python sample only; no .NET parity",
          "sampleId": sample["id"],
          "request": {"text": sample["prompt"], "width": sample["width"], "height": sample["height"],
                      "num_inference_steps": sample["steps"], "guidance_scale": sample["guidanceScale"],
                      "num_images_per_prompt": 1}, "seed": sample["seed"]}
started = time.monotonic()
try:
    snapshot = root / "artifacts" / "image-generation" / "sd15-snapshot"
    report["weights"] = verify(snapshot)
    if not report["weights"]["passed"]:
        raise RuntimeError("weight verification failed")
    import torch
    import cv2
    from modelscope.pipelines import pipeline
    model = pipeline(task="text-to-image-synthesis", model=str(snapshot),
                     trust_remote_code=False, use_safetensors=True)
    report["device"] = str(model.device)
    result = model({**report["request"], "generator": torch.Generator(device="cpu").manual_seed(sample["seed"])})
    images = result["output_imgs"]
    if len(images) != 1 or images[0].shape != (report["request"]["height"], report["request"]["width"], 3):
        raise RuntimeError("unexpected image shape")
    if not cv2.imwrite(str(output / "python.png"), images[0]):
        raise RuntimeError("PNG write failed")
    report["pixelSha256Bgr"] = hashlib.sha256(images[0].tobytes()).hexdigest()
    report["pngSha256"] = hashlib.sha256((output / "python.png").read_bytes()).hexdigest()
    report["pixelMinimum"] = int(images[0].min())
    report["pixelMaximum"] = int(images[0].max())
    report["usableForParity"] = report["pixelMaximum"] > report["pixelMinimum"]
    if not report["usableForParity"]:
        raise RuntimeError("constant image is not a usable positive parity sample")
    report["status"] = "passed"
except Exception as error:
    report["errorType"] = type(error).__name__
    raise
finally:
    report["elapsedSeconds"] = time.monotonic() - started
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
