# ONNX 图像分类适配器

`OnnxImageClassificationRuntime` 将 Base64 或 Data URI 图像转换为 ONNX `pixel_values`，并把 `[batch, labels]` logits 转换为统一的 Top-K 分类结果。它运行在 `OnnxRuntimeAdapter` 之上，不执行模型仓库代码。

## 输入输出

单图输入使用 `image`，批量输入使用 `images`：

```json
{ "images": ["iVBORw0KGgo...", "data:image/png;base64,iVBORw0KGgo..."] }
```

输出包含最佳类别、Top-K 概率、原始 logits、批量数量和标签数：

```json
{
  "predictions": [
    {
      "index": 550,
      "label": "envelope",
      "score": 0.24,
      "scores": [{ "index": 550, "label": "envelope", "score": 0.24 }]
    }
  ],
  "logits": [[0.1, 0.2]],
  "count": 1,
  "labelCount": 1001
}
```

## 兼容与安全边界

- 读取 `preprocessor_config.json`，当前支持 shortest-edge resize、双线性采样、中心裁剪、rescale 和 RGB 三通道 normalize。
- 读取 `config.json` 的 `id2label`；缺失标签使用 `LABEL_n`。
- 使用稳定 Softmax，并支持配置输出名称、Top-K、批量上限、编码字节上限和源图像像素上限。
- 在 Base64 解码前检查编码长度，并在完整解码前读取图像尺寸，降低超大输入的内存和解码风险。
- 仅接受 `[batch, labels]` 输出；非 RGB 三通道处理、自定义插值/裁剪、目标检测、分割和模型自定义后处理需要单独适配或降级到 Python Worker。
- 图像解码使用 MIT 许可证的 SkiaSharp 4.151.1，并显式携带无系统依赖的 Linux 原生资产以覆盖 Ubuntu 发布矩阵；模型自身许可证仍按发布清单独立审核。

## 认证状态

`onnx-community/mobilenet_v2_1.0_224-ONNX@ba6621a361183d7ce00314802b63a46eee2aa48b` 已与 `google/mobilenet_v2_1.0_224@4d4b64292df93eb1f5688417c18c05b23ec53630` 的 ModelScope Python/PyTorch CPU 输出完成对照。

3 个确定性 PNG 样本的 Top-1 一致率和最低 Top-5 重合率均为 100%；最大 logits 绝对误差为 `0.20605695321350104`，最大概率绝对误差为 `0.004855504748130332`，满足 `0.25`、`0.02`、Top-1 全一致和 Top-5 至少 80% 的门槛。较大的 logits 差异来自 SkiaSharp 与 Pillow 双线性缩放实现差异，因此认证同时约束概率和类别排序，不将该门槛推广到其他预处理器或硬件路径。

输入、Python 金标准、.NET 输出、认证报告和复验命令位于 `tests/compatibility/mobilenet-v2-image-classification`。认证只承诺上述固定 Revision、当前预处理配置和 ONNX Runtime CPU 路径。
