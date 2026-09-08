#!/usr/bin/env python3
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import platform
import subprocess
from pathlib import Path

import modelscope
import numpy as np
import PIL
import torch
import transformers
from modelscope import AutoImageProcessor, AutoModelForImageClassification
from PIL import Image


SOURCE_MODEL_ID = "google/mobilenet_v2_1.0_224"
SOURCE_REVISION = "4d4b64292df93eb1f5688417c18c05b23ec53630"
ONNX_MODEL_ID = "onnx-community/mobilenet_v2_1.0_224-ONNX"
ONNX_REVISION = "ba6621a361183d7ce00314802b63a46eee2aa48b"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate MobileNet V2 image classification PyTorch gold")
    parser.add_argument("--model", required=True)
    parser.add_argument("--inputs", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--modelscope-commit", required=True)
    return parser.parse_args()


def verify_modelscope_commit(expected: str) -> str:
    source_root = subprocess.check_output(
        ["git", "-C", str(Path(modelscope.__file__).resolve().parent), "rev-parse", "--show-toplevel"],
        text=True,
    ).strip()
    actual = subprocess.check_output(["git", "-C", source_root, "rev-parse", "HEAD"], text=True).strip()
    if actual != expected:
        raise RuntimeError(f"ModelScope source commit mismatch: expected {expected}, got {actual}")
    return actual


def main() -> int:
    args = parse_args()
    actual_commit = verify_modelscope_commit(args.modelscope_commit)
    input_items = json.loads(Path(args.inputs).read_text(encoding="utf-8"))
    images = [Image.open(io.BytesIO(base64.b64decode(item["base64"]))).convert("RGB") for item in input_items]
    model_path = Path(args.model).resolve()
    processor = AutoImageProcessor.from_pretrained(str(model_path), local_files_only=True)
    model = AutoModelForImageClassification.from_pretrained(str(model_path), local_files_only=True)
    model.eval()

    encoded = processor(images=images, return_tensors="pt")
    with torch.inference_mode():
        logits = model(**encoded, return_dict=True).logits
        probabilities = torch.nn.functional.softmax(logits, dim=1)
    predicted_indices = probabilities.argmax(dim=1).cpu().tolist()
    predicted_labels = [model.config.id2label[index] for index in predicted_indices]

    result = {
        "schemaVersion": 1,
        "sourceModelId": SOURCE_MODEL_ID,
        "sourceRevision": SOURCE_REVISION,
        "onnxModelId": ONNX_MODEL_ID,
        "onnxRevision": ONNX_REVISION,
        "runtime": "ModelScope AutoModelForImageClassification / PyTorch",
        "imageNames": [item["name"] for item in input_items],
        "inputSha256": [hashlib.sha256(base64.b64decode(item["base64"])).hexdigest() for item in input_items],
        "predictedIndices": predicted_indices,
        "predictedLabels": predicted_labels,
        "logits": logits.cpu().numpy().astype(np.float32).tolist(),
        "probabilities": probabilities.cpu().numpy().astype(np.float32).tolist(),
        "preprocessing": {
            "resizeShortestEdge": 256,
            "cropHeight": 224,
            "cropWidth": 224,
            "resample": "bilinear",
            "rescaleFactor": 1.0 / 255.0,
            "mean": [0.5, 0.5, 0.5],
            "standardDeviation": [0.5, 0.5, 0.5],
        },
        "environment": {
            "python": platform.python_version(),
            "modelscopeCommit": actual_commit,
            "torch": torch.__version__,
            "transformers": transformers.__version__,
            "numpy": np.__version__,
            "pillow": PIL.__version__,
        },
    }
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"python-gold-ready count={len(images)} labels={logits.shape[1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
