# nndeploy/nndeploy YOLOv5n 认证

该目录保存固定 Revision `95f0258341c1c287165bf410f62f70a751ffcab7` 的 `detect/yolov5n.onnx` 上，原项目侧 ONNX Runtime CPU + YOLOv5 letterbox 与 .NET `OnnxObjectDetectionRuntime` 的对照证据。模型权重不进入仓库。

## 验收标准

- 2 张固定 Ultralytics 演示图（zidane、bus）。
- 检测数一致，标签 100% 一致。
- 配对框最低 IoU 不低于 `0.90`。
- 分数最大绝对误差不超过 `0.05`。

当前结果：7 个检测全部配对，最低 IoU `0.9785547852516174`，最大分数误差 `0.005449891090393066`，认证通过。仅覆盖本机 macOS ARM64 CPU。

## 复验

```bash
dotnet run --project src/ModelScope.Net.Cli -- download nndeploy/nndeploy \
  --revision 95f0258341c1c287165bf410f62f70a751ffcab7 \
  --local-dir artifacts/certification/nndeploy-yolov5n \
  --allow "detect/yolov5n.onnx,configuration.json"

curl -fsSL -o artifacts/certification/yolo-zidane.jpg \
  https://raw.githubusercontent.com/ultralytics/yolov5/master/data/images/zidane.jpg
curl -fsSL -o artifacts/certification/yolo-bus.jpg \
  https://raw.githubusercontent.com/ultralytics/yolov5/master/data/images/bus.jpg

PYTHONPATH=../modelscope .certification-venv/bin/python \
  tests/compatibility/nndeploy-yolov5n/generate_python_gold.py \
  --model artifacts/certification/nndeploy-yolov5n \
  --inputs tests/compatibility/nndeploy-yolov5n/inputs.json \
  --output tests/compatibility/nndeploy-yolov5n/python-gold.json \
  --modelscope-commit 53f61360c8c10a31c7adae483c223188bb602948

dotnet run --project tools/ModelScope.Net.ObjectDetectionCertification -c Release -- \
  --onnx-model artifacts/certification/nndeploy-yolov5n \
  --inputs tests/compatibility/nndeploy-yolov5n/inputs.json \
  --gold tests/compatibility/nndeploy-yolov5n/python-gold.json \
  --output tests/compatibility/nndeploy-yolov5n/dotnet-output.json \
  --report tests/compatibility/nndeploy-yolov5n/certification-report.json
```
