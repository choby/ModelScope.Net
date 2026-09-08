# Embedding-GGUF/bge-base-en-v1.5-gguf 认证

该目录保存固定 Revision `b1a713ae4cb87f1fa7fa482a5ab339bb4fd99aa9` 的 `bge-base-en-v1.5.Q4_K_M.gguf` 上，原 ModelScope AutoModel/PyTorch CLS+L2 与 .NET GGUF `/v1/embeddings`（llama.cpp b10516、`--pooling cls`、L2）的对照证据。权重量化不是逐元素等价，因此门槛使用余弦与绝对误差。

## 验收标准

- 8 个固定文本，输出维度 768。
- CLS pooling，随后 L2 归一化。
- 最大绝对误差不超过 `0.05`。
- 每个样本余弦相似度不低于 `0.95`。

当前结果：最大绝对误差 `0.024334857240319252`，最低余弦 `0.9734526612092281`，认证通过。仅覆盖本机 macOS ARM64 CPU。

## 复验

```bash
dotnet run --project src/ModelScope.Net.Cli -- download Embedding-GGUF/bge-base-en-v1.5-gguf \
  --revision b1a713ae4cb87f1fa7fa482a5ab339bb4fd99aa9 \
  --local-dir artifacts/certification/bge-base-en-v1.5-gguf \
  --allow "bge-base-en-v1.5.Q4_K_M.gguf,configuration.json"

dotnet run --project src/ModelScope.Net.Cli -- download BAAI/bge-base-en-v1.5 \
  --revision aa1ce57e9a051ec2b7756b2d975834ad1613df5e \
  --local-dir artifacts/certification/bge-base-en-v1.5 \
  --allow "config.json,model.safetensors,tokenizer.json,tokenizer_config.json,vocab.txt,special_tokens_map.json,modules.json,sentence_bert_config.json,config_sentence_transformers.json,1_Pooling/config.json"

PYTHONPATH=../modelscope .certification-venv/bin/python \
  tests/compatibility/bge-base-en-v1.5-gguf/generate_python_gold.py \
  --model artifacts/certification/bge-base-en-v1.5 \
  --inputs tests/compatibility/bge-base-en-v1.5-gguf/inputs.json \
  --output tests/compatibility/bge-base-en-v1.5-gguf/python-gold.json \
  --modelscope-commit 53f61360c8c10a31c7adae483c223188bb602948

dotnet run --project tools/ModelScope.Net.GgufEmbeddingCertification -c Release -- \
  --executable artifacts/llama.cpp/llama-b10516/llama-server \
  --model artifacts/certification/bge-base-en-v1.5-gguf \
  --gold tests/compatibility/bge-base-en-v1.5-gguf/python-gold.json \
  --output tests/compatibility/bge-base-en-v1.5-gguf/dotnet-output.json \
  --report tests/compatibility/bge-base-en-v1.5-gguf/certification-report.json
```
