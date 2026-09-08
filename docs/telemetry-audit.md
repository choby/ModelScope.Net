# P4-02 OpenTelemetry 与审计

ModelScope.Net 在 `RuntimeRouter` 会话边界统一生成追踪、指标和审计事件。ONNX、GGUF、Python Worker 与 Remote Runtime 不需要分别埋点，直接创建适配器会话则不会自动启用本层仪表。

## 默认信号

OpenTelemetry 使用 .NET 标准 `ActivitySource` 与 `Meter`，不强制依赖或选择具体 exporter。

- ActivitySource：`ModelScope.Net.Runtime`
- Meter：`ModelScope.Net.Runtime`
- Span：`modelscope.runtime.invoke`、`modelscope.runtime.invoke_stream`
- Counter：`modelscope.runtime.invocations`、`modelscope.runtime.failures`
- Histogram：`modelscope.runtime.duration`，单位毫秒
- 审计失败 Counter：`modelscope.audit.write_failures`

应用可把公开的 Meter/ActivitySource 及四个指标名称常量接入已有 OpenTelemetry 管线：

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(ModelScopeTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(ModelScopeTelemetry.MeterName));
```

exporter、采样率、资源属性和存储周期由宿主应用决定。ModelScope.Net 不自动向外部服务发送遥测。

## 审计字段

默认 `LoggingModelScopeAuditSink` 通过结构化日志记录以下字段：

| 字段 | 含义 |
|---|---|
| `Timestamp` | UTC 完成时间 |
| `Operation` | 固定为推理操作 |
| `Outcome` | `succeeded`、`failed`、`cancelled` 或 `abandoned` |
| `Runtime` | 受控运行时名称，未知值归一为 `other` |
| `Task` | 受控任务名称，未知值归一为 `other` |
| `ModelFingerprint` | Model ID 与 Revision 的 SHA-256 前 64 bit，不记录模型路径或原始 ID |
| `Streaming` | 是否流式调用 |
| `DurationMilliseconds` | 端到端耗时 |
| `ErrorCode` | 稳定的 `ModelScopeErrorCode`、`Cancelled` 或 `Unhandled` |
| `TraceId` | 当前 Activity Trace ID；未采样时为空 |

以下数据明确禁止进入 span tag、metric tag 和默认审计事件：

- API Token、Authorization Header 与 Cookie；
- 请求 Payload、提示词、图片、张量和任意 Parameters 值；
- 模型输出、流式增量和异常 Message；
- 本地模型路径和原始私有 Model ID。

测试使用同时出现在请求、参数、输出、异常、Model ID 和路径中的哨兵秘密，逐项断言所有可观察信号均不包含该值。

## 配置

```csharp
services.AddModelScopeNet(configureTelemetry: options =>
{
    options.EnableTracing = true;
    options.EnableMetrics = true;
    options.EnableAudit = true;
    options.IncludeModelFingerprint = true;
});
```

严格匿名场景可将 `IncludeModelFingerprint` 设为 `false`，事件中将使用 `not-recorded`。追踪、指标和审计也可分别关闭。

应用可在调用 `AddModelScopeNet` 前注册自定义 `IModelScopeAuditSink`，将事件写入追加式文件、SIEM 或数据库。审计存储异常采用 fail-open：不能改变推理结果，同时递增 `modelscope.audit.write_failures` 并在当前 Activity 加入 `modelscope.audit.write_failed` 事件。生产告警必须监控该指标，避免静默丢失审计。仓库提供的 Prometheus/Grafana 接线基线及演练步骤见 [`observability-runbook.md`](./observability-runbook.md)。

## 当前边界

- SDK 不自动绑定 OTLP、Prometheus、Application Insights 或其他厂商 exporter；`deployment/observability` 仅提供可选的 Prometheus/Grafana 部署基线。
- 审计事件不是不可抵赖签名日志；防篡改、集中存储、访问控制与保留策略由部署环境负责。
- 模型指纹用于同一版本相关性分析，不应作为授权、计费或全局唯一安全标识。
- Session 创建、模型下载和管理员操作的审计尚未覆盖；本阶段验收范围是经 `RuntimeRouter` 创建的推理会话。
