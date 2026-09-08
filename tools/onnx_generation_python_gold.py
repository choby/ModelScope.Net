"""Generate deterministic greedy ONNX text-generation gold for a fixed SmolLM snapshot."""
import hashlib
import json
import pathlib
import sys
import uuid

import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer

root = pathlib.Path(sys.argv[1]).resolve()
model = root / "artifacts/onnx-text-generation/smollm-135m"
revision = "cde45563cc98f8fa03cbdf2c074985aec9efb3e5"
manifest = json.loads((model / ".modelscope-net-manifest.json").read_text())
if manifest["resolvedRevision"] != revision:
    raise ValueError("ONNX generation snapshot revision mismatch")
tokenizer = AutoTokenizer.from_pretrained(model, local_files_only=True)
session = ort.InferenceSession(str(model / "onnx/model_quantized.onnx"), providers=["CPUExecutionProvider"])
prompts = ["Once upon a time", "The capital of France is", "ModelScope makes models"]


def generate(prompt, maximum=8):
    prompt_ids = tokenizer.encode(prompt, add_special_tokens=False)
    current = np.asarray([prompt_ids], dtype=np.int64)
    past_length = 0
    cache = None
    generated = []
    for _ in range(maximum):
        feed = {"input_ids": current,
                "attention_mask": np.ones((1, past_length + current.shape[1]), dtype=np.int64),
                "position_ids": np.arange(past_length, past_length + current.shape[1], dtype=np.int64)[None, :]}
        for layer in range(30):
            for kind in ("key", "value"):
                feed[f"past_key_values.{layer}.{kind}"] = (np.zeros((1, 3, 0, 64), dtype=np.float32)
                    if cache is None else cache[f"present.{layer}.{kind}"])
        output = dict(zip([item.name for item in session.get_outputs()], session.run(None, feed)))
        token = int(np.argmax(output["logits"][0, -1]))
        generated.append(token)
        cache = output
        past_length += current.shape[1]
        current = np.asarray([[token]], dtype=np.int64)
        if token == 0:
            break
    decoded = tokenizer.decode([token for token in generated if token != 0], clean_up_tokenization_spaces=False)
    return {"prompt": prompt, "promptTokenIds": prompt_ids, "tokenIds": generated,
            "generatedText": decoded, "text": prompt + decoded,
            "finishReason": "eos" if generated[-1] == 0 else "length"}


results = [generate(prompt) for prompt in prompts]
run = root / "artifacts/onnx-text-generation/python-runs" / uuid.uuid4().hex
run.mkdir(parents=True)
report = {"schemaVersion": 1, "status": "passed", "modelId": "onnx-community/SmolLM-135M-ONNX",
          "revision": revision, "task": "text-generation", "runtime": f"onnxruntime-{ort.__version__}",
          "modelSha256": hashlib.sha256((model / "onnx/model_quantized.onnx").read_bytes()).hexdigest(),
          "vocabSha256": hashlib.sha256((model / "vocab.json").read_bytes()).hexdigest(),
          "mergesSha256": hashlib.sha256((model / "merges.txt").read_bytes()).hexdigest(), "results": results}
(run / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
print(run)
