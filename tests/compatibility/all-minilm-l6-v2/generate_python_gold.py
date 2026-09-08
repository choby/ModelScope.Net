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
from modelscope import AutoModel, AutoTokenizer


MODEL_ID = "unsloth/all-MiniLM-L6-v2"
REVISION = "4bc149651c730bffa46308d38a3f2a2c5e9b6e08"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate all-MiniLM-L6-v2 PyTorch embedding gold output")
    parser.add_argument("--model", required=True)
    parser.add_argument("--inputs", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--modelscope-commit", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    source_root = subprocess.check_output(
        ["git", "-C", str(Path(modelscope.__file__).resolve().parent), "rev-parse", "--show-toplevel"],
        text=True,
    ).strip()
    actual_commit = subprocess.check_output(
        ["git", "-C", source_root, "rev-parse", "HEAD"],
        text=True,
    ).strip()
    if actual_commit != args.modelscope_commit:
        raise RuntimeError(
            f"ModelScope source commit mismatch: expected {args.modelscope_commit}, got {actual_commit}"
        )

    model_path = Path(args.model).resolve()
    texts = json.loads(Path(args.inputs).read_text(encoding="utf-8"))
    tokenizer = AutoTokenizer.from_pretrained(str(model_path), local_files_only=True)
    model = AutoModel.from_pretrained(str(model_path), local_files_only=True)
    model.eval()

    encoded = tokenizer(
        texts,
        padding=True,
        truncation=True,
        max_length=128,
        return_tensors="pt",
    )
    with torch.inference_mode():
        hidden = model(**encoded, return_dict=True).last_hidden_state
        mask = encoded["attention_mask"].unsqueeze(-1).expand(hidden.size()).float()
        embeddings = torch.sum(hidden * mask, dim=1) / torch.clamp(mask.sum(dim=1), min=1e-9)
        embeddings = torch.nn.functional.normalize(embeddings, p=2, dim=1)

    result = {
        "schemaVersion": 1,
        "modelId": MODEL_ID,
        "revision": REVISION,
        "runtime": "ModelScope AutoModel / PyTorch",
        "pooling": "mean",
        "normalize": True,
        "maxLength": 128,
        "texts": texts,
        "inputIds": encoded["input_ids"].cpu().tolist(),
        "attentionMask": encoded["attention_mask"].cpu().tolist(),
        "embeddings": embeddings.cpu().numpy().astype(np.float32).tolist(),
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
    print(f"python-gold-ready count={len(texts)} dimensions={embeddings.shape[1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
