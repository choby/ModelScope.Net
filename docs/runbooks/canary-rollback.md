# P4-06 灰度与回滚操作手册

更新：2026-09-05。适用：单模型的 .NET 应用内发布；目标集群和 72 小时证据由 P4-07 验证。

## 接入与发布约束

生产 RuntimeRouter 还须配置 ProductionRuntimePolicy，接入方法见 ../production-runtime-policy.md；仅设置运行时 CertifiedModels 不再满足生产授权。
实现：src/ModelScope.Net.Runtime/ModelReleaseRollout.cs。每个模型/任务使用 ModelReleaseRollout 单例，宿主将普通/流式请求分别交给 InvokeAsync/InvokeStreamingAsync。直接调用 RuntimeRouter 不会自动经过灰度。

通过 VerifiedModelRelease.CreateAsync 创建发布，提供 ID、Catalog、签名、公钥、模型能力、运行时名称、摘要、平台。Catalog 必须精确绑定 Model ID、完整 40 位 Commit、任务、运行时、平台、RuntimeSha256，状态为 Certified；所有检测到的制品均须有签名校验和，并重新核验列出的文件。

RuntimeSha256 是运行时镜像、二进制或完整包（包括影响输出的配置）的摘要。宿主负责验证实际部署的是这个包，传入摘要字符串不能替代镜像验签。模型目录在验证后必须持续只读。不同发布保留各自的 Catalog、快照、配置和运行时实例。

~~~csharp
var rollout = new ModelReleaseRollout(stableRelease,
    new ModelRolloutPolicy(100, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30)));
await rollout.StageAsync(candidateRelease, goldProbe, IsWithinTolerance, cancellationToken);
var response = await rollout.InvokeAsync(opaqueRoutingKey, request, cancellationToken);
rollout.Advance(); // 本级请求数量和观察时间达标后调用
~~~

路由键使用非敏感不透明标识，通过 SHA-256 稳定分桶。快照只记录发布 ID、阶段和计数，不记录请求、输出或路由键。

## 操作流程

1. 保留旧/新模型快照、签名 Catalog、运行时包、配置与证书引用；禁止覆盖旧部署。
2. StageAsync 加载并试调用候选，数值或延迟不达标则拒绝上线。
3. 按 5%、25%、50%、100% 放量。每级均要求候选成功次数和观察时间；稳定版本请求不计入样本。100% 通过后再次 Advance 才成为稳定版本。
4. 候选运行时异常、过慢请求或不完整流自动撤回。失败仍返回调用者，不自动重放。显式调用方取消不计为成功，也不触发回滚；流消费者提前结束保守视为不完整。
5. 认证回归调用 Revoke(releaseId)。候选立即下线；稳定版本撤销时回到未撤销的上一版，无安全上一版则拒绝新请求。签名撤销分发流水线由 P5-03 跟踪。
6. Rollback 撤销候选或回到上一版。新请求切回，已开始请求/流沿原会话完成；旧阶段迟到结果不计入新阶段。宿主在排空后才停止旧 Worker/llama-server。
7. 重启时从部署系统保存的已批准稳定配置重建并验签。候选进度不自动恢复，需重新试调用及观察。GetSnapshot 是观测数据，不是可信恢复配置。
8. 多副本需要统一控制面分发配置、聚合指标与撤销；本实现不提供分布式共识或自动修改 Kubernetes。

## 复验

2026-09-06 演练工具增加 `signedRevocation` 独立检查：真实已加载 BGE 会话撤销前金标准通过、签名撤销后阻断、检查点恢复仍阻断以及旧检查点拒绝。当前完整演练输出必须使用新的目录（检查点不允许覆盖），不能复用下方历史证据目录。新证据：`artifacts/rollout/p5-03-signed-bge/report.json`。该新增检查不替代进程崩溃或生产控制面分发验收。

在解决方案目录执行：

~~~sh
dotnet run --project tools/ModelScope.Net.RolloutDrill -c Release -- artifacts/certification/bge-small-en-v1.5 tests/compatibility/bge-small-en-v1.5/python-gold.json artifacts/rollout/p4-06-bge
dotnet test tests/ModelScope.Net.Runtime.Tests -c Release --filter FullyQualifiedName~ModelReleaseRolloutTests
~~~

2026-09-05 使用 BAAI/bge-small-en-v1.5@160f4d645d32abe3cabc5af6b6b39823eadf3c0e，真实 ONNX CPU 对照 Python 8×384 金标准，逐元素绝对误差门槛 1e-4。四级放量、提升、回滚后数值一致、错误归一化阻断、认证撤销均通过。报告：artifacts/rollout/p4-06-bge/report.json。

演练使用相同固定模型与运行时的两个发布身份，证明蓝绿切换，不声称认证新 Revision。演练门槛为每级一次请求、零观察延时，不能替代生产 SLO 或长稳。演练 Catalog、签名、公钥保留，临时私钥不落盘；公钥不是生产信任根。

8 项专项测试覆盖真实 ONNX 图、故障撤回、时间门槛、请求/流排空、迟到结果、取消、不完整流、数值试调用、Catalog 不匹配、制品损坏与撤销。
