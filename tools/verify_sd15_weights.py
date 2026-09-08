"""Verify the four fixed-revision SD1.5 component weights; not model certification."""
import hashlib
import json
from pathlib import Path
import sys

REVISION = "50b8f071f400a9a501706bb7530481056f5f0558"
# Expected values from ModelScope repo/files for REVISION, not from local files.
WEIGHTS = {
    "unet/diffusion_pytorch_model.safetensors": (3438167540, "19da7aaa4b880e59d56843f1fcb4dd9b599c28a1d9d9af7c1143057c8ffae9f1"),
    "vae/diffusion_pytorch_model.safetensors": (334643276, "a2b5134f4dbc140d9c11f11cba3233099e00af40f262f136c691fb7d38d2194c"),
    "text_encoder/model.safetensors": (492265874, "d008943c017f0092921106440254dbbe00b6a285f7883ec8ba160c3faad88334"),
    "safety_checker/model.safetensors": (1215981830, "9d6a233ff6fd5ccb9f76fd99618d73369c52dd3d8222376384d0e601911089e8"),
}


def verify(root):
    results = []
    for name, (expected_size, expected_hash) in WEIGHTS.items():
        digest = hashlib.sha256()
        size = 0
        try:
            with (root / name).open("rb") as stream:
                while block := stream.read(1024 * 1024):
                    size += len(block)
                    digest.update(block)
            results.append({"path": name, "size": size, "sha256": digest.hexdigest(),
                            "passed": size == expected_size and digest.hexdigest() == expected_hash})
        except OSError as error:
            results.append({"path": name, "passed": False, "error": type(error).__name__})
    return {"modelId": "AI-ModelScope/stable-diffusion-v1-5", "revision": REVISION,
            "scope": "four component weight sizes and SHA256 only; not configs or inference",
            "passed": all(item["passed"] for item in results), "files": results}


if __name__ == "__main__":
    report = verify(Path(sys.argv[1]))
    print(json.dumps(report, indent=2))
    raise SystemExit(0 if report["passed"] else 1)
