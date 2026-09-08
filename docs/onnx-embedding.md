# ONNX Embedding 适配器

`OnnxEmbeddingRuntime` 将文本请求转换为 BERT 风格 ONNX 输入，并把模型输出转换为统一句向量。它运行在 `OnnxRuntimeAdapter` 之上，不下载模型，也不执行仓库中的 Python 代码。

## 输入输出

单文本和批量文本分别使用：

```json
{ "text": "示例文本" }
```

```json
{ "texts": ["query: 示例问题", "passage: 示例文档"] }
```

输出包含批量向量、向量维度和数量：

```json
{
  "embeddings": [[0.12, -0.34]],
  "dimensions": 2,
  "count": 1
}
```

## 当前兼容边界

- 从模型目录的 `vocab.txt` 加载 WordPiece 词表。
- 支持小写、去重音、常见 CJK 字符和标点拆分，以及 `##` 子词。
- 自动生成 `input_ids`、`attention_mask` 和 `token_type_ids`；底层 ONNX 执行器只传递模型声明的输入。
- 支持 `[batch, hidden]` 句向量，或对 `[batch, sequence, hidden]` 执行 Mean/CLS Pooling。
- Mean Pooling 使用 attention mask 排除 Padding；默认执行 L2 归一化。
- 默认最大长度 128，可通过 `OnnxEmbeddingOptions` 配置特殊 Token、输出名、池化方式和归一化。

SentencePiece、byte-level BPE、多段输入、自定义 position/task ID，以及模型特定 query 前缀不做自动猜测。需要这些能力的模型必须增加已认证 tokenizer/适配器，或降级到 Python Worker。

## 验收状态

小型可重复 ONNX 图已验证 WordPiece、Padding/Mask、Mean/CLS Pooling 和归一化。真实模型 `BAAI/bge-small-en-v1.5@160f4d645d32abe3cabc5af6b6b39823eadf3c0e` 也已完成 ModelScope Python/PyTorch 与 .NET/ONNX CPU 对照：8 个样本、384 维，最大绝对误差 `2.2996330262259335e-7`，最低余弦相似度 `0.9999999999991158`，满足 `1e-4` 与 `0.99999` 门槛。

输入、Python 金标准、.NET 输出、认证报告及复验命令位于 `tests/compatibility/bge-small-en-v1.5`。该认证只承诺固定 Revision、CLS Pooling、L2 归一化和当前 CPU 路径，不自动扩展到其他 BGE 变体、Tokenizer 或硬件提供程序。
