#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import platform
import subprocess
from pathlib import Path

import numpy as np
import torch
import transformers
from modelscope import AutoModel, AutoTokenizer


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate BGE-base PyTorch embedding gold")
    parser.add_argument("--model", required=True)
    parser.add_argument("--inputs", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--modelscope-commit", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    modelscope_root = Path(__file__).resolve().parents[3].parent / "modelscope"
    source_root = subprocess.check_output(
        ["git", "-C", str(modelscope_root), "rev-parse", "--show-toplevel"],
        text=True,
    ).strip()
    actual_commit = subprocess.check_output(["git", "-C", source_root, "rev-parse", "HEAD"], text=True).strip()
    if actual_commit != args.modelscope_commit:
        raise RuntimeError(
            f"ModelScope source commit mismatch: expected {args.modelscope_commit}, got {actual_commit}"
        )

    model_path = Path(args.model).resolve()
    texts = json.loads(Path(args.inputs).read_text(encoding="utf-8"))
    tokenizer = AutoTokenizer.from_pretrained(str(model_path), local_files_only=True)
    model = AutoModel.from_pretrained(str(model_path), local_files_only=True)
    model.eval()
    encoded = tokenizer(texts, padding=True, truncation=True, max_length=512, return_tensors="pt")
    with torch.inference_mode():
        hidden = model(**encoded, return_dict=True).last_hidden_state
        embeddings = torch.nn.functional.normalize(hidden[:, 0], p=2, dim=1)

    payload = {
        "schemaVersion": 1,
        "modelId": "Embedding-GGUF/bge-base-en-v1.5-gguf",
        "revision": "b1a713ae4cb87f1fa7fa482a5ab339bb4fd99aa9",
        "sourceModelId": "BAAI/bge-base-en-v1.5",
        "sourceRevision": "aa1ce57e9a051ec2b7756b2d975834ad1613df5e",
        "runtime": "ModelScope AutoModel / PyTorch CLS + L2",
        "pooling": "cls",
        "normalize": True,
        "maxLength": 512,
        "dimensions": int(embeddings.shape[1]),
        "texts": texts,
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
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {output} samples={len(texts)} dim={embeddings.shape[1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
