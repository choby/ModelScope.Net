# GGUF Header 与预览白名单

`GgufHeader` 按 GGUF v2/v3 的二进制布局读取 Header 和元数据，提取 `general.architecture`、`general.file_type`、`general.quantization_version`、`tokenizer.ggml.model` 与 `tokenizer.chat_template`。解析器不加载张量数据，也不执行模型代码。

## 资源与格式边界

- 默认最多读取 100 万个元数据项、200 万个数组元素和 64 MiB 元数据区。
- 单个字符串最多 16 MiB，数组最多嵌套 4 层；长度计算使用溢出检查。
- 跳过 Tokenizer token/score 等大型数组，只保留路由与安全决策所需字段。
- 校验元数据键为小写分层 ASCII 标识符，并拒绝重复键、未知类型、错误 UTF-8、损坏魔数、截断文件和不支持的版本。
- 当前按 GGUF 规范假定小端编码。仅完成 Header 验证不代表模型能被 llama.cpp 加载。

## 技术预览白名单

默认 `GgufCompatibilityPolicy` 只放行以下组合：

| 维度 | 白名单 |
|---|---|
| GGUF 版本 | 3 |
| 架构 | `qwen2` |
| 主文件类型 | `Q2_K`（值 10） |
| Tokenizer | `gpt2` |
| Chat Template | 必须存在且为字符串 |

白名单集合可以由宿主显式扩展，但扩展不等于认证。新增架构、量化或 Tokenizer 必须先加入固定 Revision 真实样本并重新生成报告。

## 认证状态

`Qwen/Qwen2.5-0.5B-Instruct-GGUF@2e50b77b0eee3083842019e257b74854323d880a` 的 `qwen2.5-0.5b-instruct-q2_k.gguf` 已完整下载并通过 SHA-256 校验。解析结果为 GGUF v3、`qwen2`、Q2_K、量化版本 2、`gpt2` Tokenizer、291 个张量、26 项元数据，且包含 Chat Template。

该结果独立满足 P3-05 的 Header 与架构白名单验收。P3-06 已在此基础上完成固定 `llama.cpp b10516`、macOS ARM64/CPU 的实际加载、文本生成、SSE 流式输出、崩溃恢复与进程清理认证；运行时结论和跨平台边界见 [GGUF llama.cpp 运行时](./gguf-runtime.md) 与 [`runtime-certification-report.json`](../tests/compatibility/qwen2.5-0.5b-instruct-gguf/runtime-certification-report.json)。
