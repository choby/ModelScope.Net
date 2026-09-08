# MobileNet V2 图像分类认证

该目录保存 `onnx-community/mobilenet_v2_1.0_224-ONNX` 固定 Revision `ba6621a361183d7ce00314802b63a46eee2aa48b` 与源模型 `google/mobilenet_v2_1.0_224@4d4b64292df93eb1f5688417c18c05b23ec53630` 的 Python/PyTorch 和 .NET/ONNX CPU 对照证据。模型权重不进入仓库。

## 验收标准

- 3 个确定性 PNG，1001 个 ImageNet 类别（含背景类）。
- shortest edge 256、双线性缩放、224×224 中心裁剪、`1/255` rescale、均值/标准差均为 `0.5`。
- 最大 logits 绝对误差不超过 `0.25`。
- 最大概率绝对误差不超过 `0.02`。
- Top-1 一致率为 100%，每个样本 Top-5 重合率至少 80%。

当前结果：最大 logits 绝对误差 `0.20605695321350104`，最大概率绝对误差 `0.004855504748130332`，Top-1 一致率和最低 Top-5 重合率均为 `1.0`，认证通过。

## 复验

先分别下载固定 ONNX 与 PyTorch 源模型快照：

```bash
dotnet run --project src/ModelScope.Net.Cli -- download \
  onnx-community/mobilenet_v2_1.0_224-ONNX \
  --revision ba6621a361183d7ce00314802b63a46eee2aa48b \
  --local-dir artifacts/certification/mobilenet-v2-onnx \
  --allow "config.json,onnx/model.onnx,preprocessor_config.json"

dotnet run --project src/ModelScope.Net.Cli -- download \
  google/mobilenet_v2_1.0_224 \
  --revision 4d4b64292df93eb1f5688417c18c05b23ec53630 \
  --local-dir artifacts/certification/mobilenet-v2-pytorch \
  --allow "config.json,model.safetensors,preprocessor_config.json"
```

生成 Python 金标准：

```bash
python3 -m venv .certification-venv
.certification-venv/bin/pip install -r tests/compatibility/mobilenet-v2-image-classification/python-requirements.txt
PYTHONPATH=../modelscope .certification-venv/bin/python \
  tests/compatibility/mobilenet-v2-image-classification/generate_python_gold.py \
  --model artifacts/certification/mobilenet-v2-pytorch \
  --inputs tests/compatibility/mobilenet-v2-image-classification/inputs.json \
  --output tests/compatibility/mobilenet-v2-image-classification/python-gold.json \
  --modelscope-commit 53f61360c8c10a31c7adae483c223188bb602948
```

运行 .NET/ONNX 对照：

```bash
dotnet run --project tools/ModelScope.Net.VisionCertification -c Release -- \
  --onnx-model artifacts/certification/mobilenet-v2-onnx \
  --source-model artifacts/certification/mobilenet-v2-pytorch \
  --inputs tests/compatibility/mobilenet-v2-image-classification/inputs.json \
  --gold tests/compatibility/mobilenet-v2-image-classification/python-gold.json \
  --output tests/compatibility/mobilenet-v2-image-classification/dotnet-output.json \
  --report tests/compatibility/mobilenet-v2-image-classification/certification-report.json
```

报告仅保留固定 Revision、文件大小和 SHA-256，不包含缓存绝对路径或访问令牌。源模型在 ModelScope 中标记为 `other` 许可证，因此进入正式发布前仍需法务确认。
