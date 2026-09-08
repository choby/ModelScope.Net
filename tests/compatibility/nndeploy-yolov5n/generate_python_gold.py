#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort


COCO80 = [
    "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
    "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
    "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
    "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
    "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
    "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
    "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
    "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
    "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
    "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier",
    "toothbrush",
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate YOLOv5 ONNX Python gold detections")
    parser.add_argument("--model", required=True)
    parser.add_argument("--inputs", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--modelscope-commit", required=True)
    parser.add_argument("--onnx-file", default="detect/yolov5n.onnx")
    parser.add_argument("--input-size", type=int, default=640)
    parser.add_argument("--confidence", type=float, default=0.25)
    parser.add_argument("--iou", type=float, default=0.45)
    return parser.parse_args()


def letterbox(image: np.ndarray, size: int) -> tuple[np.ndarray, float, float, float]:
    # Official YOLOv5 letterbox: scale uniformly, pad with 114, INTER_LINEAR.
    source_h, source_w = image.shape[:2]
    scale = min(size / source_h, size / source_w)
    resized_w = int(round(source_w * scale))
    resized_h = int(round(source_h * scale))
    pad_x = (size - resized_w) / 2.0
    pad_y = (size - resized_h) / 2.0
    resized = cv2.resize(image, (resized_w, resized_h), interpolation=cv2.INTER_LINEAR)
    canvas = np.full((size, size, 3), 114, dtype=np.uint8)
    left = int(round(pad_x - 0.1))
    top = int(round(pad_y - 0.1))
    canvas[top:top + resized_h, left:left + resized_w] = resized
    return canvas, scale, float(left), float(top)


def decode(output: np.ndarray, scale: float, pad_x: float, pad_y: float, source_w: int, source_h: int,
           confidence: float, iou: float) -> list[dict]:
    tensor = np.squeeze(output)
    if tensor.ndim != 2:
        raise RuntimeError(f"unexpected YOLO output rank {tensor.shape}")
    if tensor.shape[0] < tensor.shape[1] and tensor.shape[0] <= 84:
        tensor = tensor.T
    if tensor.shape[1] < 6:
        raise RuntimeError(f"unexpected YOLO channels {tensor.shape}")
    objectness = tensor[:, 4]
    class_scores = tensor[:, 5:]
    class_ids = class_scores.argmax(axis=1)
    scores = objectness * class_scores[np.arange(tensor.shape[0]), class_ids]
    keep = scores >= confidence
    boxes = tensor[keep, :4]
    scores = scores[keep]
    class_ids = class_ids[keep]
    if boxes.size == 0:
        return []
    cx, cy, w, h = boxes.T
    left = (cx - w / 2.0 - pad_x) / scale
    top = (cy - h / 2.0 - pad_y) / scale
    right = (cx + w / 2.0 - pad_x) / scale
    bottom = (cy + h / 2.0 - pad_y) / scale
    left = np.clip(left, 0, source_w)
    top = np.clip(top, 0, source_h)
    right = np.clip(right, 0, source_w)
    bottom = np.clip(bottom, 0, source_h)
    order = scores.argsort()[::-1]
    selected: list[int] = []
    for index in order:
        candidate = [left[index], top[index], right[index], bottom[index]]
        if all(iou_of(candidate, [left[j], top[j], right[j], bottom[j]]) <= iou
               or class_ids[index] != class_ids[j] for j in selected):
            selected.append(int(index))
            if len(selected) >= 100:
                break
    detections = []
    for index in selected:
        class_id = int(class_ids[index])
        detections.append({
            "index": class_id,
            "label": COCO80[class_id] if 0 <= class_id < len(COCO80) else f"LABEL_{class_id}",
            "score": float(scores[index]),
            "box": [float(left[index]), float(top[index]), float(right[index]), float(bottom[index])],
        })
    return detections


def iou_of(first: list[float], second: list[float]) -> float:
    left = max(first[0], second[0])
    top = max(first[1], second[1])
    right = min(first[2], second[2])
    bottom = min(first[3], second[3])
    width = max(0.0, right - left)
    height = max(0.0, bottom - top)
    intersection = width * height
    union = (first[2] - first[0]) * (first[3] - first[1]) + (second[2] - second[0]) * (second[3] - second[1]) - intersection
    return 0.0 if union <= 0 else intersection / union


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

    model_path = Path(args.model).resolve() / args.onnx_file
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    samples = json.loads(Path(args.inputs).read_text(encoding="utf-8"))
    results = []
    for item in samples:
        image_path = Path(item["path"])
        if not image_path.is_absolute():
            image_path = Path.cwd() / image_path
        encoded = image_path.read_bytes()
        array = cv2.imdecode(np.frombuffer(encoded, dtype=np.uint8), cv2.IMREAD_COLOR)
        if array is None:
            raise RuntimeError(f"could not decode {image_path}")
        rgb = cv2.cvtColor(array, cv2.COLOR_BGR2RGB)
        canvas, scale, pad_x, pad_y = letterbox(rgb, args.input_size)
        tensor = canvas.astype(np.float32) / 255.0
        tensor = np.transpose(tensor, (2, 0, 1))[None, ...]
        output = session.run(None, {input_name: tensor})[0]
        detections = decode(output, scale, pad_x, pad_y, rgb.shape[1], rgb.shape[0], args.confidence, args.iou)
        results.append({
            "name": item["name"],
            "path": item["path"],
            "width": int(rgb.shape[1]),
            "height": int(rgb.shape[0]),
            "sha256": hashlib.sha256(encoded).hexdigest(),
            "detections": detections,
        })

    payload = {
        "schemaVersion": 1,
        "modelId": "nndeploy/nndeploy",
        "revision": "95f0258341c1c287165bf410f62f70a751ffcab7",
        "onnxFile": args.onnx_file,
        "runtime": "onnxruntime CPU / YOLOv5 letterbox",
        "inputSize": args.input_size,
        "confidenceThreshold": args.confidence,
        "iouThreshold": args.iou,
        "samples": results,
        "environment": {
            "python": platform.python_version(),
            "modelscopeCommit": actual_commit,
            "onnxruntime": ort.__version__,
            "opencv": cv2.__version__,
            "numpy": np.__version__,
        },
    }
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {output} samples={len(results)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
