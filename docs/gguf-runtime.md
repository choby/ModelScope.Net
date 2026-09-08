# GGUF llama.cpp 运行时

`ModelScope.Net.Runtime.Gguf` 通过 `llama-server` 的 OpenAI 兼容 HTTP 接口调用已认证 GGUF 模型。运行时支持宿主管理子进程或连接外部服务；技术预览只承诺文本生成与 Chat Completion，不把任意 GGUF 文件视为可执行模型。

## 运行模式

### 宿主管理模式

`LocalLlamaServerSupervisor` 负责启动、健康检查、模型切换、崩溃后重新拉起和宿主退出时的进程树清理。默认启动参数包括固定模型路径、回环地址、模型别名、上下文大小、线程数、GPU 层数，并关闭 Web UI 和 slots。

```csharp
services.AddModelScopeNet(
    configureGguf: options =>
    {
        options.CertifiedModels.Add("Qwen/Qwen2.5-0.5B-Instruct-GGUF");
        options.DefaultMaxTokens = 128;
        options.MaxTokens = 1024;
    },
    createLlamaSupervisor: _ => new LocalLlamaServerSupervisor(
        new LlamaServerProcessOptions
        {
            Executable = "/opt/llama.cpp/llama-server",
            Host = "127.0.0.1",
            Port = 8080,
            ContextSize = 512,
            GpuLayers = 0,
        }));
```

管理模式只允许绑定回环 IP。若未显式提供 API Key，Supervisor 会为本次生命周期生成 256 位随机 Key，通过临时 `--api-key-file` 传给 `llama-server`；Unix 文件权限为 `0600`，释放 Supervisor 时删除。Key 不写入诊断或认证报告。

### 外部服务模式

设置 `GgufRuntimeOptions.LlamaServerEndpoint` 和 `ApiKey` 可连接由部署平台管理的 `llama-server`。非回环地址默认拒绝，只有显式设置 `AllowNonLoopbackEndpoint` 才允许；跨主机部署还必须由平台提供 TLS、Secret 管理、网络策略和服务身份，不应使用明文公网 HTTP。

## 调用契约

- `text-generation`、`completion` 调用 `/v1/completions`。
- `chat`、`chat-completion` 或包含 `messages` 的负载调用 `/v1/chat/completions`。
- 非流式返回 llama-server 的 OpenAI 兼容 JSON。
- 流式逐条解析 `data:` SSE，收到 `[DONE]` 后产生终止事件。
- 运行时覆盖客户端传入的 `model` 和 `stream`，并校验 `max_tokens`。
- 请求超时、响应体大小、流式字符数、Header 元数据均有上限。

创建会话前仍执行 [GGUF Header 白名单](./gguf-header-policy.md)。路径逃逸、不支持的任务/架构、无效 Token 上限、鉴权失败和服务错误均映射为结构化 `ModelScopeException`。

## 进程生命周期

1. 根据已检测的 GGUF 产物解析并约束模型路径。
2. 若服务未运行、已崩溃或模型发生变化，启动新的 `llama-server`。
3. 轮询 `/health`，只有模型加载完成后才创建会话。
4. 每次调用前再次确认管理进程存活；崩溃后的下一次调用自动拉起。
5. ASP.NET Core 宿主停止时，Hosted Service 终止整个子进程树。

启动失败只保留有界诊断行；不会把提示词、API Key 或完整模型路径写入认证报告。当前恢复语义是“下一次调用恢复”，不是对已经中断的生成请求透明重试。

## 固定分发与认证边界

技术预览固定 `llama.cpp b10516`、commit `b95502ba9`。各平台官方发布包、大小与 SHA-256 位于 [`deployment/llama.cpp-b10516.json`](../deployment/llama.cpp-b10516.json)。

当前实机认证范围为：

| 维度 | 已认证值 |
|---|---|
| 平台 | macOS ARM64 |
| 执行 | CPU，`n-gpu-layers=0` |
| 模型 | Qwen2.5 0.5B Instruct Q2_K |
| Revision | `2e50b77b0eee3083842019e257b74854323d880a` |
| llama.cpp | `b10516` / `b95502ba9` |
| 结果 | 加载、非流式、SSE 流式、崩溃恢复、停止清理通过 |

Ubuntu ARM64/x64 与 Windows CPU ARM64/x64 的官方二进制已经固定校验值，但尚未在对应平台执行，因此不能称为运行时认证。GPU、并发容量、长上下文、性能 SLO 和 72 小时稳定性也不属于本次 P3-06 结论。

认证证据与复验命令见 [Qwen2.5 GGUF 认证目录](../tests/compatibility/qwen2.5-0.5b-instruct-gguf/README.md)。
