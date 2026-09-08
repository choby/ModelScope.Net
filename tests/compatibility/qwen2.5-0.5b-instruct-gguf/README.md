# Qwen2.5 0.5B GGUF Header 与运行时认证

该目录保存 `Qwen/Qwen2.5-0.5B-Instruct-GGUF` 固定 Revision `2e50b77b0eee3083842019e257b74854323d880a` 的 Header、预览白名单与真实 llama.cpp 推理证据。415 MB 模型文件和 llama.cpp 二进制不进入仓库。

## 固定产物

- 文件：`qwen2.5-0.5b-instruct-q2_k.gguf`
- 大小：`415182688` 字节
- SHA-256：`9ee36184e616dfc76df4f5dd66f908dbde6979524ae36e6cefb67f532f798cb8`
- 许可证：Apache-2.0

Header 结果：GGUF v3、`qwen2`、Q2_K、量化版本 2、`gpt2` Tokenizer、291 个张量、26 项元数据并包含 Chat Template；产物校验和白名单均通过。

运行时结果：官方 `llama.cpp b10516`、commit `b95502ba9` 的 macOS ARM64 二进制和发布包校验通过；CPU 模式实际加载模型后，非流式输出 `MODELSCOPE_DOTNET_OK`、SSE 流式输出 `MODELSCOPE_STREAM_OK`，外部终止服务后下一次调用由 Supervisor 拉起新进程并输出 `MODELSCOPE_RECOVERY_OK`，最后确认进程树已停止。

## 复验

```bash
dotnet run --project src/ModelScope.Net.Cli -c Release -- download \
  Qwen/Qwen2.5-0.5B-Instruct-GGUF \
  --revision 2e50b77b0eee3083842019e257b74854323d880a \
  --local-dir artifacts/certification/qwen2.5-0.5b-instruct-gguf \
  --allow qwen2.5-0.5b-instruct-q2_k.gguf

dotnet run --project tools/ModelScope.Net.GgufCertification -c Release -- \
  --model artifacts/certification/qwen2.5-0.5b-instruct-gguf \
  --file qwen2.5-0.5b-instruct-q2_k.gguf \
  --report tests/compatibility/qwen2.5-0.5b-instruct-gguf/certification-report.json

dotnet run --project tools/ModelScope.Net.GgufRuntimeCertification -c Release -- \
  --executable artifacts/llama.cpp/llama-b10516/llama-server \
  --archive artifacts/llama.cpp/llama-b10516-bin-macos-arm64.tar.gz \
  --model artifacts/certification/qwen2.5-0.5b-instruct-gguf \
  --file qwen2.5-0.5b-instruct-q2_k.gguf \
  --report tests/compatibility/qwen2.5-0.5b-instruct-gguf/runtime-certification-report.json
```

第一个报告重新计算完整模型 SHA-256，并只保存可移植 Header 字段和 Chat Template 的长度/摘要。第二个报告还校验 llama.cpp 官方发布包、执行文件和模型，执行真实加载、确定性生成、SSE、崩溃恢复与进程清理。两份报告都不保存缓存绝对路径、提示词、API Key 或 Template 正文。

运行时认证目前仅覆盖 macOS ARM64/CPU。Ubuntu 和 Windows 官方包虽已固定校验值，但必须在对应平台重新执行后才能提升为已认证；分发清单见 [`deployment/llama.cpp-b10516.json`](../../../deployment/llama.cpp-b10516.json)。
