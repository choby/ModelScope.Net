# DistilBERT SST-2 文本分类认证

该目录保存 `distilbert/distilbert-base-uncased-finetuned-sst-2-english` 固定 Revision `ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f` 的 Python/PyTorch 与 .NET/ONNX CPU 数值对照证据。模型权重不进入仓库。

## 验收标准

- 8 个固定英文文本，2 个标签：`NEGATIVE`、`POSITIVE`。
- WordPiece、最大长度 128、批量 Padding、Softmax。
- 最大 logits 绝对误差不超过 `1e-4`。
- 最大概率绝对误差不超过 `1e-5`。
- Python 与 .NET 标签一致率为 100%。

当前结果：最大 logits 绝对误差 `3.476890563902657e-6`，最大概率绝对误差 `2.865246999661508e-7`，标签一致率 `1.0`，认证通过。

## 复验

在 `ModelScope.Net` 根目录下载固定模型文件：

```bash
dotnet run --project src/ModelScope.Net.Cli -- download \
  distilbert/distilbert-base-uncased-finetuned-sst-2-english \
  --revision ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f \
  --local-dir artifacts/certification/distilbert-sst2 \
  --allow "config.json,model.safetensors,onnx/model.onnx,tokenizer_config.json,vocab.txt"
```

建立隔离 Python 环境并生成金标准：

```bash
python3 -m venv .certification-venv
.certification-venv/bin/pip install -r tests/compatibility/distilbert-sst2/python-requirements.txt
PYTHONPATH=../modelscope .certification-venv/bin/python \
  tests/compatibility/distilbert-sst2/generate_python_gold.py \
  --model artifacts/certification/distilbert-sst2 \
  --inputs tests/compatibility/distilbert-sst2/inputs.json \
  --output tests/compatibility/distilbert-sst2/python-gold.json \
  --modelscope-commit 53f61360c8c10a31c7adae483c223188bb602948
```

运行 .NET/ONNX 对照：

```bash
dotnet run --project tools/ModelScope.Net.ClassificationCertification -c Release -- \
  --model artifacts/certification/distilbert-sst2 \
  --gold tests/compatibility/distilbert-sst2/python-gold.json \
  --output tests/compatibility/distilbert-sst2/dotnet-output.json \
  --report tests/compatibility/distilbert-sst2/certification-report.json
```

认证生成器核对实际加载的 ModelScope 源码 Commit；报告仅保留固定 Revision、文件大小和 SHA-256，不包含缓存绝对路径或访问令牌。
