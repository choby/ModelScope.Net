# ONNX 目标检测适配器

`OnnxObjectDetectionRuntime` 将 Base64 或 Data URI 图像按 YOLOv5/YOLOv8 常见导出图做 letterbox 预处理，并把检测头解码为 `[left, top, right, bottom]` 框、分数和标签。它运行在 `OnnxRuntimeAdapter` 之上，不执行模型仓库代码。

## 输入输出

单图输入使用 `image`，与图像分类相同：

```json
{ "image": "data:image/png;base64,iVBORw0KGgo..." }
```

输出同时提供类型化 `detections` 和与 ModelScope Pipeline 对齐的 `boxes` / `scores` / `labels`：

```json
{
  "detections": [
    { "index": 0, "label": "person", "score": 0.72, "box": [4, 4, 12, 12] }
  ],
  "boxes": [[4, 4, 12, 12]],
  "scores": [0.72],
  "labels": ["person"],
  "count": 1
}
```

## 兼容与安全边界

- 默认 letterbox 到 640×640，填充值 114，再按 `1/255` 缩放到 `[0,1]`；当前预览只接受 batch=1。
- 支持两种检测头：YOLOv5 `[1, num, 5+classes]`（含 objectness）和 YOLOv8 `[1, 4+classes, num]`。
- 分数低于 `ConfidenceThreshold` 的候选丢弃；同类框按 IoU 做贪心 NMS，并限制 `MaxDetections`。
- 坐标映射回原图像素；超出图像边界的框被裁剪。
- 读取可选 `config.json` 的 `id2label`；缺失或空标签回退到 COCO 80 类名称。
- 在 Base64 解码前检查编码长度，并在完整解码前读取图像尺寸。
- 不承诺任意自定义锚框、分割头、多尺度融合或模型仓库后处理脚本。

## 认证状态

`nndeploy/nndeploy@95f0258341c1c287165bf410f62f70a751ffcab7` 的 `detect/yolov5n.onnx` 已与独立 Python ONNX Runtime + YOLOv5 letterbox 金标准对照：zidane/bus 共 7 个检测全部配对，最低 IoU `0.97855`，最大分数误差 `0.00545`，标签一致率 1.0。报告见 `tests/compatibility/nndeploy-yolov5n/`。认证只承诺该固定 Revision、本机 macOS ARM64 CPU 和当前 letterbox/NMS 配置。
