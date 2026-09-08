# P4 运行时 Dashboard 与告警基线

## 结论

仓库提供可导入 Grafana 的运行时 Dashboard、Prometheus 告警规则和机器可读指标契约。它们覆盖当前 `RuntimeRouter` 实际产生的推理调用量、失败率、延迟分位数和审计写入失败，不宣称覆盖尚未埋点的下载、排队、内存、GPU、Worker 重启或 API 配额。

资产位于：

- `deployment/observability/metric-contract.json`：OpenTelemetry 名称、Prometheus 导出名称、有限标签和默认门槛；
- `deployment/observability/grafana-dashboard.json`：调用率、失败率、p50/p95/p99 和审计写失败；
- `deployment/observability/prometheus-rules.yaml`：审计丢失、失败率和 p95 延迟告警。

## 接入前提

宿主必须订阅 `ModelScope.Net.Runtime` Meter，并将指标交给兼容 Prometheus 的 OpenTelemetry exporter。该基线假定 exporter 将点号转换为下划线、为 Counter 添加 `_total`，并把 `ms` 规范为 `_milliseconds`。不同 collector/exporter 配置改变名称时，必须同时修改指标契约、Dashboard 和告警规则；自动化测试会检查三者仍引用同一组指标。

应用层仍按遥测文档接入 Meter：

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(ModelScopeTelemetry.MeterName));
```

Exporter、抓取端点、认证、TLS、保留期和高可用由部署平台配置，不由 SDK 自动开启。导入 Dashboard 后选择 Prometheus 数据源；规则文件加载到 Prometheus 或兼容规则引擎。

## 上线核对

1. 在隔离环境发起一次成功调用和一次受控失败，确认调用 Counter 增加、失败 Counter 只增加一次。
2. 确认 duration Histogram 存在 `_bucket`、`_count` 和 `_sum` 序列，Dashboard 的 p95 查询有数据。
3. 使用测试 Audit Sink 触发一次写入失败，确认推理仍成功且 `ModelScopeAuditWritesFailing` 立即进入 firing；随后恢复真实 Sink。
4. 检查导出标签只有受控的 Runtime、Task、Streaming 和 Outcome；不得出现 Model ID、路径、提示词或模型输出。
5. 用目标平台、任务和业务流量批准的 SLO 替换 2.5 秒/10 秒及 5%/15% 的便携默认门槛，再纳入值班路由。
6. 将告警接到灰度/回滚手册；只有与发布、容量或运行时健康证据相关联后才自动回滚，避免低流量噪声触发破坏性动作。

## 默认规则语义

失败率告警要求调用速率大于 0.1 次/秒，避免极低流量下单次失败造成持续告警。无流量不会被解释为成功。审计 Sink 是 fail-open，因此任何写入失败均为 Critical。延迟门槛采用跨任务保守默认值，仅用于部署接线验证，不是产品 SLA，也不能替代 `P4-07` 的目标平台 72 小时测量。

## 已知缺口

当前仪表不能直接生成迁移方案列出的下载吞吐/重试/续传/缓存命中、模型加载与排队、内存/显存、Worker 重启/OOM、路由降级及远程额度图表。生产 SLO、告警接收人、静默窗口和升级路径也需要由产品/SRE 批准。因而本基线关闭“没有 Dashboard/告警资产”的工程缺口，但不关闭 FR-10 或最终生产验收。
