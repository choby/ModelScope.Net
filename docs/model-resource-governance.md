# P4-04 模型资源治理

P4-04 在 `RuntimeRouter` 之上提供统一的 `ModelSessionPool`。资源池负责 Session 预热与复用、非活跃项 LRU 驱逐、全局及单模型并发门禁、有限等待队列、估算内存配额和空闲回收。ASP.NET Core 集成将资源池注册为宿主级单例，并提供不含模型身份和本地路径的健康数据。

## 默认策略

| 控制项 | 默认值 | 含义 |
|---|---:|---|
| 缓存 Session 上限 | 5 | 超出时先驱逐最久未使用的非活跃 Session |
| Session 估算内存总额 | 3 GiB | 新 Session 在加载前执行准入检查 |
| 全局活动租约 | 1 | CPU 技术预览默认串行执行 |
| 单模型活动租约 | 1 | 同一 Session 默认不并发调用 |
| 等待队列 | 32 | 队列满时快速失败，不无限堆积请求 |
| 排队超时 | 30 秒 | 超时返回可重试的 `ResourceQuotaExceeded` |
| 空闲回收时间 | 15 分钟 | 由调用方定期执行 `TrimAsync` 回收 |

默认值来自 P3-08 macOS ARM64/CPU 容量基线，是保守的技术预览起点，不是跨平台 SLA。GPU、Windows 和 Linux 应在各自真实基准完成后单独配置。

## ASP.NET Core 接入

```csharp
builder.Services.AddModelScopeNet(configureResources: options =>
{
    options.MaxCachedSessions = 5;
    options.MaxEstimatedMemoryBytes = 3L * 1024 * 1024 * 1024;
    options.MaxConcurrentLeases = 1;
    options.MaxConcurrentLeasesPerModel = 1;
    options.MaxQueuedAcquisitions = 32;
    options.QueueTimeout = TimeSpan.FromSeconds(30);
    options.IdleTimeout = TimeSpan.FromMinutes(15);
});
```

`ModelSessionPool` 是单例。长生命周期服务应从依赖注入取得该实例，不应为每个请求创建资源池，也不应绕过资源池直接创建生产 Session。

## 预热和调用

调用方先通过 Hub 下载固定 Revision，再由 `ModelInspector` 得到 `ModelCapabilities`。每个请求必须提供按目标模型和运行时测量或保守估算的内存值。

```csharp
var sessionRequest = new ModelSessionPoolRequest(
    capabilities,
    EstimatedMemoryBytes: 768L * 1024 * 1024,
    PreferredRuntime: "onnx");

await pool.PrewarmAsync([sessionRequest], cancellationToken);

await using var lease = await pool.AcquireAsync(sessionRequest, cancellationToken);
var response = await lease.Session.InvokeAsync(modelRequest, cancellationToken);
```

必须释放租约。租约存续期间，该 Session 不会被 LRU 或空闲清理驱逐；排队但已保留的获取请求也不会在授予前被误驱逐。资源池关闭表示宿主正在停止，此后现有 Session 不应继续使用。

## 驱逐、排队和错误语义

- 新模型超过缓存项或估算内存上限时，只驱逐已加载成功且没有活动/待授予租约的最久未使用 Session。
- 没有可驱逐项、单模型估算已超过完整配额、队列已满或排队超时，均返回 `ModelScopeException`，错误码为 `ResourceQuotaExceeded`，并标记为可重试。
- 加载失败的缓存项立即删除；后续请求可以重新加载，不会永久缓存失败。
- `TrimAsync` 只清理超过空闲时间且无活动/待授予租约的 Session。
- `GetSnapshot` 和 `modelscope-resource-pool` 健康检查只公开不可逆模型指纹及容量计数，不公开 Model ID、Revision 或文件路径。

队列负责背压，不负责自动微批处理。文本或图像批处理仍由任务适配器和调用方按 P3-08 建议组织。

## 内存与安全边界

`EstimatedMemoryBytes` 是准入估算，不是实时 RSS、显存或操作系统强制限制。估算过低仍可能导致进程 OOM；托管堆、原生库、页缓存、下载缓存、日志和 ASP.NET Core 自身开销也不在该数字内。

因此 P4-04 只关闭应用层资源治理；P4-05 已补充 cgroup/容器 CPU、内存、进程数和 GPU 配额，以及只读模型挂载、网络策略和非 root 沙箱。目标集群是否实际执行这些限制仍须在 P4-07 验证。GGUF 技术预览继续保持每个 llama-server 一个活动生成请求；提高 slots 前必须重新执行并发与稳定性基准。

## 验收证据

- `ModelSessionPoolTests`：12 项资源池行为与并发竞态测试。
- `ServiceCollectionExtensionsTests`：资源池配置、单例注册和健康检查测试。
- `release-manifest-v1.yaml`：冻结技术预览默认资源策略。
- `deployment/worker-security-matrix.json`：应用层治理及 P4-05 OS 级部署控制均已完成。
