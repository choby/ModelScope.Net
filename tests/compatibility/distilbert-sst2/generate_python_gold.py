#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import platform
import subprocess
from pathlib import Path

import modelscope
import numpy as np
import torch
import transformers
from modelscope import AutoModelForSequenceClassification, AutoTokenizer


MODEL_ID = "distilbert/distilbert-base-uncased-finetuned-sst-2-english"
REVISION = "ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate the DistilBERT SST-2 PyTorch gold file")
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
    actual = subprocess.check_output(
        ["git", "-C", source_root, "rev-parse", "HEAD"],
        text=True,
    ).strip()
    if actual != expected:
        raise RuntimeError(f"ModelScope source commit mismatch: expected {expected}, got {actual}")
    return actual


def main() -> int:
    args = parse_args()
    actual_commit = verify_modelscope_commit(args.modelscope_commit)
    model_path = Path(args.model).resolve()
    texts = json.loads(Path(args.inputs).read_text(encoding="utf-8"))
    tokenizer = AutoTokenizer.from_pretrained(str(model_path), local_files_only=True)
    model = AutoModelForSequenceClassification.from_pretrained(str(model_path), local_files_only=True)
    model.eval()

    encoded = tokenizer(
        texts,
        padding=True,
        truncation=True,
        max_length=128,
        return_tensors="pt",
    )
    with torch.inference_mode():
        logits = model(**encoded, return_dict=True).logits
        probabilities = torch.nn.functional.softmax(logits, dim=1)

    labels = [model.config.id2label[index] for index in range(model.config.num_labels)]
    predicted_indices = probabilities.argmax(dim=1).cpu().tolist()
    result = {
        "schemaVersion": 1,
        "modelId": MODEL_ID,
        "revision": REVISION,
        "runtime": "ModelScope AutoModelForSequenceClassification / PyTorch",
        "scoreMode": "softmax",
        "maxLength": 128,
        "texts": texts,
        "labels": labels,
        "predictedLabels": [labels[index] for index in predicted_indices],
        "inputIds": encoded["input_ids"].cpu().tolist(),
        "attentionMask": encoded["attention_mask"].cpu().tolist(),
        "logits": logits.cpu().numpy().astype(np.float32).tolist(),
        "probabilities": probabilities.cpu().numpy().astype(np.float32).tolist(),
        "environment": {
            "python": platform.python_version(),
            "modelscopeCommit": actual_commit,
            "torch": torch.__version__,
            "transformers": transformers.__version__,
            "numpy": np.__version__,
        },
    }
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"python-gold-ready count={len(texts)} labels={len(labels)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
