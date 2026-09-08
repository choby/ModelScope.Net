# all-MiniLM-L6-v2 ONNX Embedding 认证

该目录保存 `unsloth/all-MiniLM-L6-v2` 固定 Revision `4bc149651c730bffa46308d38a3f2a2c5e9b6e08` 的 Python/PyTorch 与 .NET/ONNX CPU 数值对照证据。模型采用 Apache-2.0 许可证，权重不进入仓库。

## 验收标准

- 8 个固定英文文本，输出维度 384。
- Attention Mask Mean Pooling，随后执行 L2 归一化。
- 最大绝对误差不超过 `1e-4`。
- 每个样本的余弦相似度不低于 `0.99999`。

当前结果：最大绝对误差 `2.0932153224975658e-7`，最低余弦相似度 `0.9999999999992116`，认证通过。

## 复验

```bash
dotnet run --project src/ModelScope.Net.Cli -- download unsloth/all-MiniLM-L6-v2 \
  --revision 4bc149651c730bffa46308d38a3f2a2c5e9b6e08 \
  --local-dir artifacts/certification/all-minilm-l6-v2 \
  --allow "config.json,1_Pooling/config.json,config_sentence_transformers.json,model.safetensors,modules.json,onnx/model.onnx,sentence_bert_config.json,special_tokens_map.json,tokenizer.json,tokenizer_config.json,vocab.txt"

PYTHONPATH=../modelscope .certification-venv/bin/python \
  tests/compatibility/all-minilm-l6-v2/generate_python_gold.py \
  --model artifacts/certification/all-minilm-l6-v2 \
  --inputs tests/compatibility/all-minilm-l6-v2/inputs.json \
  --output tests/compatibility/all-minilm-l6-v2/python-gold.json \
  --modelscope-commit 53f61360c8c10a31c7adae483c223188bb602948

dotnet run --project tools/ModelScope.Net.Certification -c Release -- \
  --model artifacts/certification/all-minilm-l6-v2 \
  --gold tests/compatibility/all-minilm-l6-v2/python-gold.json \
  --output tests/compatibility/all-minilm-l6-v2/dotnet-output.json \
  --report tests/compatibility/all-minilm-l6-v2/certification-report.json
```

认证报告只保存固定 Revision、校验值、数值误差和可移植环境信息，不保存本机缓存路径或访问凭据。
