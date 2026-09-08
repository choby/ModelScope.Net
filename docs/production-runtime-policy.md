# P2-04 生产路由认证与远程回退

更新：2026-09-05。默认生产授权现在来自 ProductionRuntimePolicy，不能仅用运行时的 CertifiedModels 名单声明。

## 接入

~~~csharp
var policy = await ProductionRuntimePolicy.LoadAsync(
    catalogPath, signaturePath, publicKeyPem, platformId,
    new Dictionary<string, string> { ["onnx-embedding"] = deployedRuntimeDigest });
await policy.ApplyRevocationsAsync(revocationListPath, revocationSignaturePath);
services.AddModelScopeNet(configureRouter: options =>
{
    options.Mode = RuntimePolicyMode.Production;
    options.ProductionPolicy = policy;
    options.AllowRemoteFallback = false;
});
~~~

LoadAsync 先验签并复制调用方运行时摘要映射。策略不能通过公开构造器伪造；创建 Router 后，修改原 options 或摘要字典不会改变现有授权。

2026-09-06 起，新策略必须先接受有效签名撤销清单才能用于创建会话。缺失或过期时拒绝运行，即便启用实验性运行，或运行时未出现在部署摘要映射中。宿主须在有效期内刷新清单；库不会自动联网获取。

签名条目须精确匹配 Model ID、40 位 Commit、任务、运行时、平台、RuntimeSha256，状态为 Certified。运行时自报 Certified 不再足以授权；有精确认证的 Compatible/Detected 运行时可以加载，Unavailable/Unsupported 始终拒绝。

创建会话前校验签名清单的全部制品 Hash、已检测制品覆盖及路径边界；损坏/缺失/未列出的制品导致 DownloadIntegrityFailed，不尝试远程回退。纯 Remote 条目无本地制品时可省略文件清单，但服务部署摘要和固定模型身份仍必须认证。宿主负责确认所传摘要对应实际部署、只读快照及受信密钥；模型服务是否真正固定了版本须在该服务的认证测试中证明。

生产会话的普通和流式调用只能使用该会话的任务；会话池缓存键纳入任务与远程代码标记，防止复用绕过路由。Catalog 是不可变快照：Catalog 或部署变化后，创建新的策略、路由和池，经灰度替换旧实例。现有实例不会后台读取新 Catalog，但支持通过 `ApplyRevocationsAsync` 应用同一信任根签名的追加式撤销，缓存会话与执行中的响应也会复核。撤销分发、重启防重放等剩余门禁见 [认证撤销](certification-revocation.md)。

## 远程调用

- 自动路由优先尝试本地运行时，默认不使用远程；即使只有远程运行时也不自动外发。
- preferredRuntime 明确选择 Remote（或实现 IRemoteModelRuntime 的插件）属于显式远程调用。
- AllowRemoteFallback=true 才允许自动远程兜底。开发模式可在可重试本地加载失败后尝试远程。
- 生产模式的可重试本地加载失败只会在显式允许时转向独立精确认证的远程目标；不继续隐式切换其他本地版本。
- 指定本地 preferredRuntime 后，即使允许回退也不会切到远程。
- 自定义外部服务插件必须实现 IRemoteModelRuntime；内建 remote 名称也被识别。进程管理器和插件本身仍属于受信部署代码。
- AllowExperimentalInProduction 是已有的显式实验例外：它允许 Compatible/LoadVerified，不能把结果当成 Certified，也不适用于正式发布验收。默认 false。

## 证据

18 项 ProductionRuntimePolicyTests，连同既有路由测试、会话池边界及全套运行时回归通过；运行时总计 105 项。完整修复验证为 Hub18 + Runtime105 + ASP.NET9 + Cli5 = 137 项。

artifacts/rollout/p2-04-policy-bge/report.json：固定 BGE 模型在新生产策略下完成真实四级放量、回滚后 Python 金标准对照、数值回归阻断及撤销演练。

兼容性变化：此前仅配置 CertifiedModels 的生产宿主现在会拒绝加载；须接入签名策略。开发模式的自动远程行为也改为默认拒绝，显式 Remote 调用保持可用。
