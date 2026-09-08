# BAAI/bge-small-en-v1.5 认证

该目录保存固定 Revision `160f4d645d32abe3cabc5af6b6b39823eadf3c0e` 的 Python/PyTorch 与 .NET/ONNX CPU 数值对照证据。模型权重不进入仓库，下载后位于被忽略的 `artifacts/certification` 目录。

## 验收标准

- 8 个固定文本，输出维度 384。
- CLS Pooling，随后执行 L2 归一化。
- 最大绝对误差不超过 `1e-4`。
- 每个样本的余弦相似度不低于 `0.99999`。

当前结果：最大绝对误差 `2.2996330262259335e-7`，最低余弦相似度 `0.9999999999991158`，认证通过。

## 复验

在 `ModelScope.Net` 根目录下载固定模型文件：

```bash
dotnet run --project src/ModelScope.Net.Cli -- download BAAI/bge-small-en-v1.5 \
  --revision 160f4d645d32abe3cabc5af6b6b39823eadf3c0e \
  --local-dir artifacts/certification/bge-small-en-v1.5 \
  --allow "config.json,1_Pooling/config.json,config_sentence_transformers.json,model.safetensors,modules.json,onnx/model.onnx,sentence_bert_config.json,special_tokens_map.json,tokenizer.json,tokenizer_config.json,vocab.txt"
```

建立隔离的 Python 环境，并使用迁移源项目生成金标准：

```bash
python3 -m venv .certification-venv
.certification-venv/bin/pip install -r tests/compatibility/bge-small-en-v1.5/python-requirements.txt
PYTHONPATH=../modelscope .certification-venv/bin/python \
  tests/compatibility/bge-small-en-v1.5/generate_python_gold.py \
  --model artifacts/certification/bge-small-en-v1.5 \
  --inputs tests/compatibility/bge-small-en-v1.5/inputs.json \
  --output tests/compatibility/bge-small-en-v1.5/python-gold.json \
  --modelscope-commit 53f61360c8c10a31c7adae483c223188bb602948
```

运行 .NET/ONNX 对照并刷新报告：

```bash
dotnet run --project tools/ModelScope.Net.Certification -c Release -- \
  --model artifacts/certification/bge-small-en-v1.5 \
  --gold tests/compatibility/bge-small-en-v1.5/python-gold.json \
  --output tests/compatibility/bge-small-en-v1.5/dotnet-output.json \
  --report tests/compatibility/bge-small-en-v1.5/certification-report.json
```

`certification-report.json` 只保留模型 Revision、文件大小和 SHA-256，不包含本机缓存路径或访问令牌。
