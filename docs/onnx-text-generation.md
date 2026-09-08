# ONNX 文本生成适配器

`OnnxTextGenerationRuntime` 为经过逐模型认证的 decoder-only ONNX 图提供进程内 CPU 贪心生成。运行时使用仓库内的 `vocab.json` 和 `merges.txt` 执行 byte-level BPE，维护 KV cache，并提供普通响应和逐 token 流式响应。

## 调用

```bash
dotnet run --project src/ModelScope.Net.Cli -- run ./models/smollm-135m \
  --runtime onnx-text-generation \
  --task text-generation \
  --payload '{"prompt":"Once upon a time","maxNewTokens":8}'
```

`--prompt "Once upon a time"` 使用默认的 16 个新 token。ASP.NET Core 中运行时默认注册，也可配置边界：

```csharp
services.AddModelScopeNet(configureOnnxTextGeneration: options =>
{
    options.MaxPromptTokens = 64;
    options.DefaultMaxNewTokens = 16;
    options.MaxNewTokens = 32;
    options.MaxContextTokens = 96;
});
```

请求 payload 可为字符串，或仅含 `prompt`/`text` 二选一及可选 `maxNewTokens` 的对象。未知字段、额外通用 parameters、空 token 序列和超限上下文都会在执行前拒绝。普通响应返回完整文本、增量文本、token ID、计数和结束原因；流式响应每个 token 一个 `data` 事件，最后恰好一个 `done` 事件。取消令牌在每一步推理前检查。

## 已认证图契约

当前证据只覆盖 `onnx-community/SmolLM-135M-ONNX@cde45563cc98f8fa03cbdf2c074985aec9efb3e5` 的 `onnx/model_quantized.onnx`，SHA-256 为 `7ae6828aa72763890cfc729cc0a84adeaa848bc0cba834a2aedbbcc02681936e`。图为 `LlamaForCausalLM`，30 层、3 个 KV head、head dimension 64，输入为 `input_ids`、`attention_mask`、`position_ids` 及 30 组 past key/value，结束 token 为 0。

独立 Python ONNX Runtime 1.29.0 金标准报告位于 `artifacts/onnx-text-generation/python-runs/6608d1c7c8f74fc2a88b50776d6ebb8b/report.json`；.NET 报告位于 `artifacts/onnx-text-generation/dotnet-runs/376c39fa4fe54ec6a06478c697d04965/report.json`。三个提示词的前 8 个贪心 token ID 和文本完全一致；重复执行确定，流式为 8 个 data 加 1 个 done。

## 边界

- 这是逐模型认证的技术预览，不承诺任意 causal ONNX 导出可直接运行。
- 当前仅支持 batch 1、贪心解码和固定 KV cache 命名；不支持 sampling、beam search、聊天模板或动态读取任意模型结构。
- 当前通过原始张量 JSON 适配层传递 logits 和 cache，便于复用安全边界但不是高吞吐实现；上下文上限保持较小。
- 证据仅覆盖本机 macOS ARM64 CPU。Windows、Linux、CUDA、容器、长稳、吞吐和更大模型仍需独立认证。
- `Compatible` 只表示结构和资产可尝试加载；生产 `Certified` 仍须由签名策略精确绑定模型 ID、Commit、任务、平台、运行时和制品哈希。
