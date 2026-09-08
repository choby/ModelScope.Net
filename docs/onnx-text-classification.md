# ONNX 文本分类适配器

`OnnxTextClassificationRuntime` 将单个或批量文本转换为 BERT 风格 ONNX 输入，并将 `[batch, labels]` logits 转换为统一分类结果。它运行在 `OnnxRuntimeAdapter` 之上，不执行模型仓库代码。

## 输入输出

输入支持字符串、`text` 或 `texts`：

```json
{ "texts": ["I loved this movie.", "This was disappointing."] }
```

输出包含最佳标签、按得分排序的候选、原始 logits、批量数量和标签数：

```json
{
  "predictions": [
    {
      "index": 1,
      "label": "POSITIVE",
      "score": 0.999,
      "scores": [
        { "index": 1, "label": "POSITIVE", "score": 0.999 },
        { "index": 0, "label": "NEGATIVE", "score": 0.001 }
      ]
    }
  ],
  "logits": [[-3.1, 3.8]],
  "count": 1,
  "labelCount": 2
}
```

## 兼容边界

- 与 Embedding 适配器共享 WordPiece，实现小写、去重音、CJK/标点拆分、截断和批量 Padding/Mask。
- 从 `config.json` 的 `id2label` 或 `label2id` 读取标签；缺失时使用 `LABEL_n`。
- 默认对单标签分类执行稳定 Softmax，也可配置 Sigmoid 处理独立多标签分数。
- 支持配置输出名称、最大长度和 Top-K；保留 logits 用于诊断和数值认证。
- 仅接受 `[batch, labels]` 分类输出，并验证批量与张量长度。
- SentencePiece、BPE、文本对、多段 token type、自定义后处理和动态标签体系需要单独适配或降级到 Python Worker。

## 认证状态

`distilbert/distilbert-base-uncased-finetuned-sst-2-english@ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f` 已完成 ModelScope Python/PyTorch 与 .NET/ONNX CPU 对照。8 个样本标签一致率 100%，最大 logits 绝对误差 `3.476890563902657e-6`，最大概率绝对误差 `2.865246999661508e-7`，满足 `1e-4`、`1e-5` 和全标签一致门槛。

输入、金标准、.NET 输出、认证报告和复验命令位于 `tests/compatibility/distilbert-sst2`。认证只承诺该固定 Revision、Softmax、当前 WordPiece 和 CPU 路径。
