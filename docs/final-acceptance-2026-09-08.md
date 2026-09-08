# ModelScope.Net 最终验收复核（2026-09-08）

## 决策

**生产 1.0：NO-GO。技术预览工程候选：本机范围通过。**

当前代码、测试、固定模型证据、部署基线、Dashboard/告警和本地 NuGet 预览包已经形成可审查的工程候选；但发布清单与许可证未获批准，真实私有仓库、目标 Windows/Linux/GPU、生产防回退存储、远程 CI、7 天 Hub SLO、72 小时稳定性、镜像签名/当前漏洞扫描及三方 Go/No-Go 均无合格证据。任何单元测试总数都不能替代这些门槛。

## 本轮最终验证

| 检查 | 结果 | 证据 |
|---|---|---|
| Release 全解决方案构建 | 通过，0 warning / 0 error | 本轮命令输出 |
| .NET 自动化 | Hub 43 + Runtime 201 + ASP.NET Core 11 + CLI 5 = 260，通过 260、失败 0、跳过 0 | `artifacts/acceptance/p4-observability-distribution/*.trx` |
| Python 工具自动化 | 12/12 通过 | certification、upstream monitor、NuGet verifier 测试 |
| NuGet 包集合 | 8 nupkg + 8 snupkg，版本均为 `0.1.0-preview.1`，内容/依赖验证通过 | `artifacts/packages/`；`tools/verify_nuget_packages.py` |
| 包消费者 | 只从本地包源恢复顶层 ASP.NET Core 包并编译，0 warning / 0 error | `tests/ModelScope.Net.PackageSmoke/` |
| Dashboard/告警 | JSON 可解析，四项实际指标、有限标签、全部查询及 Critical 审计写失败由测试绑定 | `deployment/observability/`；`ObservabilityAssetsTests` |

## 原 Python 源代码与批准迁移范围

原代码基线包含 Hub 查询/下载/凭据、动态 Pipeline、大量任务实现、数据集、训练、上传及管理能力。本迁移计划明确采用多运行时而非逐行重写：Hub 下载和目标推理路径在范围内；训练、微调、数据集、上传管理和“纯 .NET 执行任意社区模型”不在首发范围。因此这些未移植域是已声明范围差异，不是当前实现遗漏。

在首发范围内：

| 域 | 源代码对照结论 | 当前状态 |
|---|---|---|
| Hub 查询、版本、文件/快照、过滤、缓存 | 原 `hub/api.py`、`file_download.py`、`snapshot_download.py` 的目标子集已有原生 .NET 入口，固定 Revision、Hash、续传、并发与离线缓存有证据 | 工程完成；真实私有/受限仓库与 7 天 SLO 未验 |
| 动态 Pipeline 推理 | 不复制 Python 注册表；通过 ONNX、GGUF、隔离 Python Worker 和 Remote 统一路由 | 已覆盖认证模型/任务；不承诺任意 Pipeline |
| 远程代码 | 原 Python 可动态加载；.NET 默认拒绝，只有固定 Commit、显式批准后交给隔离 Worker | 策略实现完成；生产不可信沙箱和审批未完成 |
| 模型上传、数据集、训练/微调 | 原代码存在，迁移计划明确排除 | 不属于验收缺口 |

## 方案与源代码逐项差异

| 方案项 | 实际源代码 | 验收判断 |
|---|---|---|
| ONNX CPU/CUDA | 当前 `InferenceSession` 只走默认 CPU；没有 CUDA Execution Provider 接线 | CPU 预览通过；GPU/CUDA 未完成，保持 fail-closed |
| ONNX 任务适配器 | Embedding、文本分类、图像分类、SmolLM 贪心文本生成已实现 | P3“图像分类或检测”已满足；通用目标检测适配器仍无，候选明确 Unsupported |
| GGUF 任务 | Header 白名单、llama.cpp 文本生成/流式/恢复已实现 | 计划中的 GGUF Embedding 尚无，候选明确 Unsupported |
| Python Worker | gRPC、回环鉴权、进程恢复、资源准入及图像生成/OCR/ASR真实本机证据已实现 | 生产容器、GPU、更多模型和执行中无损重放不声明 |
| ASP.NET Core | 提供 DI、健康检查、资源池、Telemetry 和审计；没有网关 API 端点 | 因没有自带 API 面，未内置终端用户鉴权/限流/租户配额；资源并发配额已实现。宿主鉴权方案和租户口径需产品/SRE决定 |
| 可观测性 | 当前推理会话有调用、失败、耗时和审计写失败；新增 Dashboard/规则 | 下载、缓存、模型加载/排队、内存/GPU、Worker重启/OOM、路由降级和远程额度信号仍缺 |
| 审计范围 | 推理完成事件和安全字段策略已实现 | 下载、Session创建、管理员操作、不可抵赖存储仍缺 |
| 容器/多架构 | Kustomize CPU/GPU清单、隔离策略和本机 Linux/ARM64 基础镜像证据存在 | 目标 x64/GPU/Windows、签名、多架构发布清单和当前镜像扫描未完成 |
| NuGet/升级 | 8 个对齐预览包、符号包、CI pack/验证、消费者及升级手册已补齐 | 未推送公共源；无包签名、SBOM/provenance、1.0 owner批准或干净远程 runner 证据 |
| 灰度/回滚/撤销 | 本机固定 BGE、签名 Catalog、撤销及存储崩溃恢复有证据 | 文件存储不抗管理员整体回退；生产控制面、分发和集群故障未验 |

## 关闭生产验收所需输入与执行

以下工作不能由当前本机源代码继续推导或伪造：

1. 产品、技术、安全、法务批准 15～25 模型清单、任务范围、许可证和用户可见 SLO。
2. 提供一个获授权的真实私有/受限仓库及短期安全凭据，执行成功/拒绝对照且不记录 Token。
3. 冻结目标 Windows/Linux x64、GPU/CUDA/驱动/Kubernetes/CNI 矩阵并提供 runner/集群。
4. 接入生产防回退可信存储、签名密钥管理、Catalog/撤销分发和告警接收人。
5. 在远程 CI 运行 build/test/certification/package smoke；在候选镜像上完成被授权的漏洞、许可证、签名、SBOM/provenance 检查。
6. 连续执行 Hub 7 天/至少 2000 次的 99.5% 契约 SLO，以及目标平台 72 小时推理稳定性、故障注入、容量和灰度回滚。
7. 汇总证据后由产品、技术、安全负责人签署 Go/No-Go；只有全部为通过才能将版本提升为 1.0。

## 可复现入口

状态和历次证据见 `docs/migration-progress.md` 与 `docs/acceptance-audit.md`。本轮新增分发说明为 `docs/distribution.md`，升级步骤为 `docs/upgrading.md`，监控接线为 `docs/observability-runbook.md`。当前 NO-GO 是缺失生产证据和批准的结果，不是本轮回归失败。
