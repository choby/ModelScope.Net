"""Create a Python gold record for one fixed local ModelScope ASR snapshot."""
import hashlib
import json
import pathlib
import sys
import uuid
import wave

import numpy as np
from modelscope.pipelines import pipeline

root = pathlib.Path(sys.argv[1]).resolve()
model_path = root / "artifacts/asr/paraformer-snapshot"
audio_path = model_path / "example/asr_example.wav"
manifest = json.loads((model_path / ".modelscope-net-manifest.json").read_text())
revision = "ff922d0e9af830cee4d1ec9b57b193196941efd8"
if manifest["resolvedRevision"] != revision:
    raise ValueError("ASR snapshot revision mismatch")
with wave.open(str(audio_path), "rb") as source:
    if (source.getnchannels(), source.getsampwidth(), source.getframerate()) != (1, 2, 16000):
        raise ValueError("ASR certification input format mismatch")
    waveform = np.frombuffer(source.readframes(source.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
model = pipeline(task="auto-speech-recognition", model=str(model_path), trust_remote_code=False, disable_update=True)
raw = model(waveform, audio_fs=16000)
if not isinstance(raw, list) or len(raw) != 1 or not isinstance(raw[0].get("text"), str):
    raise ValueError("Unexpected ASR output")
run = root / "artifacts/asr/python-runs" / uuid.uuid4().hex
run.mkdir(parents=True)
result = {"schemaVersion": 1, "modelId": "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-pytorch",
          "revision": revision, "task": "auto-speech-recognition", "inputSha256": hashlib.sha256(audio_path.read_bytes()).hexdigest(),
          "output": {"text": raw[0]["text"]}, "rawShape": "single-item-list", "status": "passed"}
(run / "report.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
print(run)
