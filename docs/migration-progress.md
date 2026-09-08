# ModelScope.Net 迁移进度表

> 这是迁移任务的唯一进度台账。每次中断前、恢复后、构建或验收后都必须更新“状态、证据、最后更新”三列。

## 状态约定

| 状态 | 含义 |
|---|---|
| NOT_STARTED | 尚未开始 |
| IN_PROGRESS | 已开始但未达到验收条件 |
| BLOCKED | 存在明确外部阻塞，阻塞说明必须可操作 |
| DEFERRED | 经决策推迟，不属于当前发布范围 |
| COMPLETED | 实现与自动化验收均完成 |

## 当前检查点

| 项目 | 当前值 |
|---|---|
| 当前迁移阶段 | 2026-09-08 P5-04 yolov5n 与 bge-base GGUF Embedding 本机金标准通过。生产1.0仍为NO-GO：私库、平台矩阵、72小时、远程CI与三方批准未提供 |
| 当前发布目标 | 技术预览版 |
| 目标框架 | .NET 10 |
| 最近基线 | ModelScope Python commit 53f61360（2026-08-31） |
| 最近更新时间 | 2026-09-08 |
| 恢复入口 | 读取 docs/acceptance-audit.md 与 docs/final-acceptance-2026-09-08.md。本机可执行迁移已完成对照；关闭1.0需要外部私库、平台、72小时、远程CI和签字 |

## 总体进度

| 阶段 | 完成/总数 | 状态 | 退出条件 |
|---|---:|---|---|
| 0 范围与基线 | 3/8 | IN_PROGRESS | release-manifest-v1.yaml 获产品、技术、安全批准 |
| 1 Hub MVP | 10/11 | IN_PROGRESS | 下载、续传、缓存、私有鉴权和契约测试通过 |
| 2 运行时骨架 | 8/8 | COMPLETED | inspect、Remote、Python Worker端到端及通用生产策略工程验收通过；真实环境最终核对仍在进行 |
| 3 原生推理 | 8/8 | COMPLETED | 5～10 个固定 Revision 模型认证及预览性能门禁通过 |
| 4 生产化 | 6/8 | IN_PROGRESS | SLO、安全、回滚和 72 小时稳定性测试通过 |
| 5 持续扩展 | 0/5 | IN_PROGRESS | 按版本持续执行；P5-01本机工程验证通过，远程CI待验 |

## 任务台账

| ID | 阶段 | 任务 | 状态 | 依赖 | 验收标准 | 证据/产物 | 最后更新 |
|---|---:|---|---|---|---|---|---|
| P0-01 | 0 | 技术可行性分析 | COMPLETED | - | 技术方案完成读者测试 | docs/technical-solution.md | 2026-09-01 |
| P0-02 | 0 | 迁移路线与人力计划 | COMPLETED | P0-01 | 迁移计划完成读者测试 | docs/migration-plan.md | 2026-09-01 |
| P0-03 | 0 | 固定 .NET/OS 基线 | COMPLETED | - | .NET 10 基线写入方案并在本机验证 | global.json；dotnet SDK 10.0.300 | 2026-09-01 |
| P0-04 | 0 | 选择 15～25 个代表模型 | IN_PROGRESS | P0-01 | 每个模型固定 ID/Commit/任务/许可证 | 15个固定Revision草案。yolov5n与bge-base GGUF已本机认证；Qwen-VL/Qwen-72B仍为元数据候选。待业务批准并提供真实私有/受限仓库；多个许可证仍待复核 | 2026-09-08 |
| P0-05 | 0 | 建立金标准数据与 Python 输出 | IN_PROGRESS | P0-04 | 样本和容差可自动执行 | 既有模型外新增 yolov5n 与 bge-base GGUF Embedding 的固定输入、Python金标准与.NET报告；Qwen-VL/Qwen-72B仍无运行金标准 | 2026-09-08 |
| P0-06 | 0 | 冻结 CPU/GPU/CUDA 矩阵 | IN_PROGRESS | P0-04 | 技术与 SRE 批准 | deployment/platform-validation-matrix.json已记录实际SDK/依赖与拟验平台；GPU/CUDA/驱动组合及目标主机待提供，未批准 | 2026-09-05 |
| P0-07 | 0 | 威胁模型与许可证流程 | IN_PROGRESS | P0-04 | 安全/法务批准 | docs/security/threat-model.md与license-review-workflow.md形成可审查材料，绑定16项控制及生产认证；正式安全/法务批准仍待提供 | 2026-09-05 |
| P0-08 | 0 | 批准 1.0 发布清单 | BLOCKED | P0-04..07 | 产品、技术、安全共同批准 | 阻塞：需业务模型清单和负责人签字 | 2026-09-01 |
| P1-01 | 1 | 创建解决方案与包结构 | COMPLETED | P0-03 | 解决方案包含计划中的全部项目 | ModelScope.Net.sln；src/tests/samples | 2026-09-01 |
| P1-02 | 1 | 统一构建、代码规范和版本配置 | COMPLETED | P1-01 | restore/build 使用固定 SDK | global.json；Directory.Build.props | 2026-09-01 |
| P1-03 | 1 | Model ID、Endpoint、Token、Revision 解析 | COMPLETED | P1-01 | 单元测试覆盖有效和恶意输入 | ModelIdTests；Bearer 模拟契约 | 2026-09-01 |
| P1-04 | 1 | 模型信息和文件列表查询 | COMPLETED | P1-03 | 模拟契约测试通过 | Hub 测试；公开 Qwen 元数据查询通过 | 2026-09-01 |
| P1-05 | 1 | 单文件下载 | COMPLETED | P1-04 | Range/取消/进度/校验通过 | Hub 测试覆盖续传、取消、进度、SHA | 2026-09-01 |
| P1-06 | 1 | 快照并发下载 | COMPLETED | P1-05 | allow/ignore 和 maxWorkers 通过 | allow/ignore、并发上限和在线过滤下载通过；过滤重下与完整 Manifest 合并，不再覆盖未触及文件 | 2026-09-03 |
| P1-07 | 1 | Blob/Snapshot/Manifest 缓存 | COMPLETED | P1-05 | 复用、原子提交、多进程安全通过 | 多进程/过滤重下测试通过；本轮补离线模型和Revision匹配、读取锁及allow/ignore结果过滤，拒绝错误本地快照 | 2026-09-05 |
| P1-08 | 1 | 私有/受限仓库与凭据存储 | IN_PROGRESS | P1-04 | 无 Token 泄漏且鉴权错误明确 | 0600/0700 原子凭据存储、Bearer、401 映射完成；待真实私有仓库授权下载 | 2026-09-01 |
| P1-09 | 1 | CLI login/info/download/cache | COMPLETED | P1-04..08 | CLI 端到端测试通过 | login/logout/info/download/cache scan/verify 冒烟通过 | 2026-09-01 |
| P1-10 | 1 | Hub 单元和契约测试 | COMPLETED | P1-03..09 | 本地测试全绿 | Hub 18 项测试通过，含离线身份及过滤回归 | 2026-09-05 |
| P1-11 | 1 | 在线下载冒烟测试 | COMPLETED | P1-10,P0-04 | 固定公开仓库下载成功 | Qwen/Qwen2.5-0.5B-Instruct@186d855... config.json 在线与离线通过 | 2026-09-01 |
| P2-01 | 2 | 模型格式与配置检测 | COMPLETED | P1-06 | ONNX/GGUF/Python 配置样本通过 | ModelInspectorTests | 2026-09-01 |
| P2-02 | 2 | 能力状态模型与诊断 | COMPLETED | P2-01 | Detected/Compatible/LoadVerified 可区分 | RuntimeModels.cs；检测测试 | 2026-09-01 |
| P2-03 | 2 | IModelRuntime/IModelSession | COMPLETED | P2-02 | API 编译并有替身测试 | RuntimeContracts.cs；FakeRuntime 测试 | 2026-09-01 |
| P2-04 | 2 | 开发/生产 RuntimeRouter | COMPLETED | P2-03 | 优先级、策略和回退测试通过 | ProductionRuntimePolicy验签并精确绑定ID/Commit/任务/平台/运行时摘要及制品Hash；默认禁止自动Remote，显式选择/允许后才外发；会话任务边界及缓存身份保护；18项策略测试、既有回归及真实BGE生产策略灰度复验通过；docs/production-runtime-policy.md | 2026-09-05 |
| P2-05 | 2 | Remote API Runtime | COMPLETED | P2-03 | 非流式与 SSE 流式测试通过 | RemoteRuntimeTests；run CLI | 2026-09-01 |
| P2-06 | 2 | Python Worker 协议和客户端 | COMPLETED | P2-03 | 健康、调用、崩溃恢复通过 | gRPC 健康/加载/调用/流式/卸载及崩溃恢复通过；`allow_remote_code` 映射 `trust_remote_code`；参数类型强制转换；ONNX `ModelLoadFailed` 可重试并在开发模式降级到 Python；CLI `--python-worker`/`--runtime python` 可拉起本地 Worker | 2026-09-03 |
| P2-07 | 2 | inspect/run CLI | COMPLETED | P2-01..06 | CLI 对固定样本输出稳定 | 固定 Commit inspect 通过；本地 `run MODEL_DIRECTORY` 经 inspect/RuntimeRouter 调用 ONNX；ONNX 加载失败可降级到 `--python-worker` echo/modelscope；GGUF 通过 `--llama-server` 接入；`owner/name` 或 `--runtime remote` 保留 API Inference | 2026-09-03 |
| P2-08 | 2 | 兼容性 Catalog | COMPLETED | P0-04,P2-02 | 可加载签名清单并拒绝篡改 | RSA-PSS Catalog loader、篡改/移动分支拒绝测试、catalog 示例 | 2026-09-01 |
| P3-01 | 3 | ONNX Runtime 执行器 | COMPLETED | P2-03 | Session/Tensor/CPU 测试通过 | ONNX Runtime 1.29.0 CPU 会话、原始 JSON 张量、形状/资源限制、结构化错误及有效 ONNX 图推理测试通过 | 2026-09-01 |
| P3-02 | 3 | Embedding 适配器 | COMPLETED | P3-01,P0-05 | 金标准通过 | BGE-small-en-v1.5 固定 Revision 完成 ModelScope Python/PyTorch 与 .NET/ONNX CPU 8×384 对照；最大绝对误差 2.30e-7，最低余弦 0.999999999999；复验工具和报告已固化 | 2026-09-01 |
| P3-03 | 3 | 文本分类适配器 | COMPLETED | P3-01,P0-05 | 金标准通过 | 共享 WordPiece、批处理、标签映射、Softmax/Sigmoid、Top-K、ASP.NET DI 完成；DistilBERT SST-2 固定 Revision 8 样本标签 100% 一致，最大 logits 误差 3.48e-6、概率误差 2.87e-7 | 2026-09-01 |
| P3-04 | 3 | 视觉适配器 | COMPLETED | P3-01,P0-05 | 图像分类或检测通过 | 图像解码、short-edge resize、中心裁剪、RGB 归一化、批处理、Top-K、输入资源限制与 ASP.NET DI 完成；MobileNet V2 固定 ONNX/源 Revision 的 3 样本 Top-1 与最低 Top-5 均 100%，最大 logits 误差 0.2061、概率误差 0.004856；Linux x64 发布产物含原生解码资产 | 2026-09-01 |
| P3-05 | 3 | GGUF Header 与架构白名单 | COMPLETED | P2-01 | 固定 GGUF 样本通过 | 有界 GGUF v2/v3 元数据解析、损坏/溢出/重复键防护及严格预览白名单完成；Qwen2.5 0.5B Q2_K@2e50b77... 完整 415182688 字节与 SHA-256 校验通过，识别 v3/qwen2/Q2_K/gpt2/Chat Template；后续 P3-06 实际加载要求已满足 | 2026-09-02 |
| P3-06 | 3 | llama.cpp 受控进程 | COMPLETED | P3-05 | 生成、流式、崩溃恢复通过 | Runtime.Gguf 已实现管理/外部服务、回环限制、临时随机 API Key、健康检查、OpenAI/SSE、资源上限、崩溃后拉起与进程树清理；固定 llama.cpp b10516/macOS ARM64/CPU 实际加载 Qwen2.5 0.5B Q2_K，三组确定性输出、流式终止与恢复通过 | 2026-09-02 |
| P3-07 | 3 | 5～10 个模型认证 | COMPLETED | P3-02..06 | 5～10 个固定 Revision 模型的技术认证报告全绿；发布批准由 P0-08 跟踪 | BGE、all-MiniLM、DistilBERT SST-2、MobileNet V2、Qwen2.5 GGUF 共 5 个本地模型通过；统一门禁校验 Revision、任务覆盖、报告回链与状态，发布清单仍为 draft 且不代表审批完成 | 2026-09-02 |
| P3-08 | 3 | 性能和容量报告 | COMPLETED | P3-07 | CPU 预览门槛明确；未认证 GPU 保持 fail-closed | `tests/performance/p3-08-cpu-macos-arm64.json`；`docs/performance-capacity.md`；`artifacts/benchmarks/p3-08-report-app/dist/index.html`；2 轮、5 模型、9 场景全绿 | 2026-09-02 |
| P4-01 | 4 | ASP.NET Core 集成 | COMPLETED | P2-04 | DI、health、hosted service | Hub/Remote/Python/Router、ONNX 三类适配器及 GGUF DI 完成；Python 与 llama-server HostedService 可清理子进程，3 项集成测试通过 | 2026-09-02 |
| P4-02 | 4 | OpenTelemetry 与审计 | COMPLETED | P4-01 | 无敏感输入泄漏 | RuntimeRouter 会话统一仪表、ActivitySource/Meter、结构化审计、自定义 Sink、审计失败指标；哨兵测试覆盖请求/参数/输出/异常/私有 ID/路径均不泄漏；Prometheus指标契约、Grafana Dashboard及告警规则由自动测试绑定实际指标名；`docs/telemetry-audit.md`、`docs/observability-runbook.md` | 2026-09-08 |
| P4-03 | 4 | Worker 安全隔离 | COMPLETED | P2-06,P0-07 | 安全矩阵通过 | 11 项应用控制与 5 项部署控制全部通过；只读挂载、默认拒绝网络、restricted 非 root/seccomp、OS 资源配额和真实双向 TLS 均有机器可读清单及自动化证据 | 2026-09-02 |
| P4-04 | 4 | 模型资源治理 | COMPLETED | P2-04 | 预热、LRU、并发、配额 | `ModelSessionPool`、ASP.NET Core DI/健康检查、`docs/model-resource-governance.md`；12 项资源池测试覆盖活跃/待授予租约保护、LRU、全局/单模型并发、有限队列、超时、内存准入、回收及共享加载失败 | 2026-09-02 |
| P4-05 | 4 | 容器与部署清单 | COMPLETED | P4-01..04 | Windows/Linux CPU 和 Linux GPU | Linux CPU/GPU Kustomize、非 root 只读 Worker 镜像、默认拒绝 NetworkPolicy、CPU/内存/临时存储/PID/GPU 配额、Secret 挂载及 mTLS 完成；本机 Linux/ARM64 协议基础镜像固定 Alpine digest 构建并受限运行，Docker Scout 43 包全等级 0 漏洞；Windows CPU 仅允许认证 ONNX/Remote，本地 Python/未知代码 fail-closed；目标 x64/GPU/集群运行验证仍由 P4-07 关闭；`docs/deployment-p4-05.md` | 2026-09-02 |
| P4-06 | 4 | 灰度与回滚 | COMPLETED | P4-05 | 演练通过 | ModelReleaseRollout 签名绑定、四级放量、门槛、故障撤回、请求排空及撤销；8 项专项测试和真实 BGE 8×384 金标准蓝绿/回滚/数值阻断通过；docs/runbooks/canary-rollback.md；artifacts/rollout/p4-06-bge/report.json；集群验证仍归P4-07 | 2026-09-05 |
| P4-07 | 4 | 72 小时稳定性测试 | NOT_STARTED | P4-01..06 | 通过发布清单 SLO | artifacts/stability | 2026-09-01 |
| P4-08 | 4 | 1.0 Go/No-Go | BLOCKED | P0-08,P4-07 | 三方批准 | 阻塞：前置阶段未完成 | 2026-09-01 |
| P5-01 | 5 | 新模型认证流水线 | IN_PROGRESS | P3-07 | 可重复认证 | tools/certification_pipeline.py与certification-plan.json实现五模型固定制品/金标准/运行时Hash、全新报告、数值行为门禁和中断留痕；6项负面测试及两轮真实五模型全绿；.github/workflows/model-certification.yml已配置，远程runner未执行；docs/continuous-certification.md | 2026-09-05 |
| P5-02 | 5 | 上游变化监控 | IN_PROGRESS | P5-01 | Revision/依赖变化可检测 | tools/upstream_monitor.py及依赖Hash基线、每日CI入口已实现；9项流水线测试通过；六仓库在线检查发现Qwen新增LICENSE/README/FP16及.gitattributes变化，固定Q2_K复验通过；远程CI未运行；docs/upstream-monitoring.md | 2026-09-05 |
| P5-03 | 5 | 认证撤销和回归 | IN_PROGRESS | P5-01 | 回归自动降级 | 签名撤销/新鲜度/会话复核/历史恢复、更新前持久暂存、单调提交、丢失确认重试及启动对账完成；真实BGE在暂存后进程exit -9，新进程自动提交并继续拒绝撤销模型。完整258项通过。文件实现仅供单机演练；生产防回退外部存储、自动分发及目标部署故障待验；docs/certification-revocation.md | 2026-09-08 |
| P5-04 | 5 | 新任务适配器 | IN_PROGRESS | P3-01 | 按业务价值持续交付 | 图像生成、OCR、ASR、SmolLM、nndeploy YOLOv5n（7检测/最低IoU 0.978）与bge-base GGUF Embedding（8×768/最低余弦0.973）均有本机固定Revision证据。持续扩展仍开放；生产矩阵未验 | 2026-09-08 |
| P5-05 | 5 | 社区插件流程 | DEFERRED | P4-08 | 签名、审核和版本政策 | 1.0 后评估 | 2026-09-01 |

## 中断恢复检查

1. 读取本表，确认当前阶段和第一个 IN_PROGRESS 任务。
2. 运行 dotnet --info，确认 global.json 指定的 SDK 可用。
3. 运行 dotnet test ModelScope.Net.sln。
4. 检查 git diff 或目标目录文件变更，避免覆盖未验收工作。
5. 从首个 IN_PROGRESS 任务继续；完成后更新证据和总体计数。

## 最近一次验证证据

2026-09-08 P5-04 金标准：`nndeploy/nndeploy@95f0258...` yolov5n 与独立 Python ONNX Runtime letterbox 对照通过（2图7检测，最低IoU 0.97855，分数误差 0.00545）；`Embedding-GGUF/bge-base-en-v1.5-gguf@b1a713a...` Q4_K_M 经 llama.cpp b10516 `/v1/embeddings` 与 ModelScope AutoModel CLS+L2 对照通过（8×768，最低余弦 0.97345，最大绝对误差 0.02433）。CLI `run` 两次检测与两次 embedding 向量一致。Release 回归 Hub43+Runtime209+ASP.NET11+CLI5=268 项通过。生产1.0仍为NO-GO。

2026-09-08 P5-04 原生适配器增量：新增 `OnnxObjectDetectionRuntime`（YOLOv5 `[1,N,5+C]` / YOLOv8 `[1,4+C,N]`、letterbox、objectness×class、同类 NMS、原图像素框）和 GGUF Embedding（`/v1/embeddings`、`text`/`prompt`→`input`、独立 `bert`/`Q4_K_M` Header 白名单）。CLI/DI/Windows CPU 策略/inspect 任务推断已接线。Release 回归 Hub43+Runtime207+ASP.NET11+CLI5=266 项通过。合成 ONNX 图与 HTTP 契约测试覆盖该范围；`nndeploy/yolov5n` 与 `Embedding-GGUF/bge-base-en-v1.5-gguf` 仍未做真实金标准，不得称为 Certified。

2026-09-08 最终源代码/方案复核：生产1.0 NO-GO，本机技术预览工程候选通过，详见`docs/final-acceptance-2026-09-08.md`。Release构建0警告/0错误；Hub43+Runtime201+ASP.NET11+CLI5=260项通过、0失败/0跳过，TRX在`artifacts/acceptance/p4-observability-distribution/`；Python工具12项通过。新补8个`0.1.0-preview.1` nupkg及8个snupkg，验证器和只引用本地顶层包的消费者均通过、0警告。生产关闭条件仍是批准清单/许可证/SLO/平台、真实私库、防回退存储与分发、远程CI/供应链、7天Hub和72小时长稳及三方签字。

2026-09-08 P4可观测性工程补齐：新增`deployment/observability`机器可读指标契约、Grafana Dashboard和Prometheus规则，覆盖推理调用率、失败率、p50/p95/p99及任何审计写失败；公开四个指标名称常量并新增2项资产一致性/敏感标签负面测试。完整回归260项通过。默认阈值仅用于部署接线，不是批准SLO；下载、排队、资源、Worker及远程额度尚无对应运行时指标，生产exporter/接收人和P4-07长稳仍待外部环境。

2026-09-08 P5-03存储型撤销协调：新增`IRevocationPublicationStore`、单机`FileRevocationPublicationStore`、更新前暂存的Apply重载、存储专用幂等Retry及`LoadWithRevocationPublicationStoreAsync`启动对账。4项新增策略案例通过；完整Release Hub43+Runtime201+ASP.NET9+CLI5=258项通过、0失败/0跳过，TRX在`artifacts/acceptance/p5-03-revocation-store/`。最终程序集真实BGE故障报告`artifacts/rollout/revocation-store-crash-547949e27cbe4ac4bc33214832de86ef/crash-report.json`通过：写进程在序号2撤销持久暂存后exit -9，新进程自动对账、提交检查点、阻断撤销模型并拒绝旧检查点。文件存储不抗管理员整体回退，非生产信任根；真实外部存储及目标部署仍是P5-03关闭条件。

2026-09-08 P5-04 ONNX文本生成：新增byte-level BPE、decoder-only KV cache贪心生成、类型化普通/流式响应、输入与上下文上限、CLI/ASP.NET DI/Telemetry/Windows CPU策略接线。固定`onnx-community/SmolLM-135M-ONNX@cde45563cc98f8fa03cbdf2c074985aec9efb3e5`量化图SHA-256 `7ae6828aa72763890cfc729cc0a84adeaa848bc0cba834a2aedbbcc02681936e`；Python报告`artifacts/onnx-text-generation/python-runs/6608d1c7c8f74fc2a88b50776d6ebb8b/report.json`与.NET报告`artifacts/onnx-text-generation/dotnet-runs/376c39fa4fe54ec6a06478c697d04965/report.json`在3个提示词×8 token上ID和文本精确一致，重复确定，流式8 data+1 done；真实CLI的payload、`--prompt`和流式路径均通过。Release全套Hub43+Runtime197+ASP.NET9+CLI5=254项通过、0失败/0跳过，TRX在`artifacts/acceptance/p5-04-onnx-text-generation/`。只覆盖本机macOS ARM64 CPU、batch1、greedy、固定30层图和短上下文，不代表任意ONNX生成、生产性能或跨平台认证；P5-04保持IN_PROGRESS。

2026-09-08 P0代表集合增量：将5个已认证P5模型及制品Hash回填`release-manifest-v1.yaml`，并通过ModelScope真实Revision/详情接口固定4个工程候选：`Qwen/Qwen2.5-VL-3B-Instruct@1b5a...`远程多模态、`Qwen/Qwen-72B@5c0f...`需远程代码、`Embedding-GGUF/bge-base-en-v1.5-gguf@b1a7...`及`nndeploy/nndeploy@95f0...`目标检测。后两项按当前源代码明确标记Unsupported，不伪造加载/推理结果。清单达到15个固定Revision的数量下限；这不是业务批准、私库验证、许可证审批或新增模型认证，P0-04保持IN_PROGRESS。

2026-09-07 中期范围复核（图像生成增量之前）：同步 docs/acceptance-audit.md 的FR-01/06/08/09和P2/P5总表，纠正早期记录遗漏已完成工程证据的问题。当时未重跑测试，最近完整回归为235项。没有7天/2000次Hub契约SLO或72小时推理稳定性证据，不能将当前五模型与局部诊断改进视为全部迁移完成。

2026-09-07 P5-04启动：用户确认OCR而非PCR，按图像生成、OCR、语音识别、ONNX文本生成推进。Worker将原ModelScope的BGR数组转换为PNG，限制最多4张、合计4Mi像素和2MiB编码内容；5项Python单元测试通过。此证据只覆盖输出编码，不代表真实图像生成认证或输入计算资源隔离已完成。新增模块已加入Dockerfile、CLI及测试输出复制清单；旧容器镜像需重新构建验证，旧镜像证据不覆盖当前源码。此前五模型复认证全部通过，报告为 artifacts/certification-runs/9c3438f489ef4ebfa7b84d1bc69f5703/summary.json，其源码哈希先于本次Worker修改。

2026-09-07 P5-04 .NET请求增量：新增 Runtime.Python/ImageGenerationRequest，将提示词、负面提示词、尺寸、步数、引导系数和张数映射到原 stable_diffusion_pipeline.py 的 text/negative_prompt/width/height/num_inference_steps/guidance_scale/num_images_per_prompt，固定PIL及字典输出。限制提示词4096字符、尺寸64–2048且8对齐、最多4张及合计4Mi像素、1–100步、有限0–30引导系数。3项定向.NET测试通过（映射、非法输入、包含边界）。此时仅为类型化入口校验，后续Worker增量见下。

2026-09-07 P5-04 Worker输入准入：在模型调用之前校验图像输入，拒绝未知字段、额外parameters、布尔值冒充整数、非有限引导系数及超限张数/尺寸/步数；规范默认值与.NET请求一致，文本长度按UTF-16计数。普通及流式入口均覆盖，非图像任务保留原契约。10项Python测试通过，包含Mock模型执行前拒绝测试（不是真实模型认证）；执行命令：`PYTHONPATH=.worker-model-deps PYTHONDONTWRITEBYTECODE=1 .certification-venv/bin/python -m unittest discover -s worker/python -p test_image_output.py -v`。首次未带侧载依赖时因缺grpc失败，使用既有侧载依赖后通过，未安装或替换金标准环境。Release完整.NET回归238项通过、0跳过（Hub43/Runtime181/AspNetCore9/CLI5），TRX保存在 artifacts/acceptance/p5-04-image-input/。参数准入不保证实际CPU/GPU内存充足或强制取消计算；类型化输出解析、确定性种子、真实模型与容器重认证仍未完成。

任务检测衔接增量：显式任务优先，缺失时仅按一致的明确架构后缀推断并提示，未知/冲突不猜测。8项新增测试，完整 Hub43 + Runtime178 + ASP.NET9 + Cli5 =235项通过，0失败/0跳过，`artifacts/acceptance/fr06-task-inference/`；固定DistilBERT无 --task/--runtime 的真实CLI分类冒烟通过，不代表提升认证状态或最终验收。

目录扫描增量完整 Release 回归：Hub43 + Runtime170 + ASP.NET9 + Cli5 =227项通过、0失败/0跳过，`artifacts/acceptance/fr06-scan-limits/`。可配置条目/深度限额、取消、忽略目录剪枝及目录链接拒绝已实现；缓存文件链接保留，实际 DistilBERT CLI inspect 通过。不是防并发路径替换的OS沙箱，其他运行入口资源治理仍待逐项核对。

2026-09-07 核心元数据读取限额：配置1 MiB/清单16 MiB，超限拒绝，损坏或非对象配置保守标记未知需审核，JSON警告不回显原始异常。10项新增案例，完整 Release 回归 Hub43 + Runtime164 + ASP.NET9 + Cli5 =221项通过、0失败/0跳过，`artifacts/acceptance/fr06-metadata-final/`。尚不覆盖全目录文件数量限额和所有OS异常脱敏，不表示FR-06或最终验收完成。

2026-09-07 FR-06 README 许可证增量：完整 Release 回归 Hub43 + Runtime154 + ASP.NET9 + Cli5 =211项通过，0失败/0跳过，`artifacts/acceptance/fr06-readme-keys/`。19个新增案例覆盖顶层简单标识/列表及来源、重复/不可信结构、正文与嵌套不误识别、读取限额；仅声明提取，不能代替法务批准或通用YAML/SPDX解析，FR-06仍为部分完成。

发布串行化与取消等待者专项补验后，完整 Release 回归 Hub43 + Runtime135 + ASP.NET9 + Cli5 = 192 项通过，0 失败/0 跳过，`artifacts/acceptance/p5-03-publication-serialized/`。取消排队请求不能解除正在进行的发布阻断，第二次签名更新按序提交。

P5-03 真实 BGE 发布故障演练通过：`artifacts/rollout/p5-03-publication-bge/report.json` 的 durablePublication 验证未撤销模型在发布等待/失败期间仍阻断缓存和新会话，保存不能绕过，确认重试后旧/新会话均恢复 8×384 金标准。生产“内存接受后、可信锚提交前”进程终止窗口尚未关闭，需要独立持久化发布意图及恢复对账等协议；已请求可信存储测试环境。

P5-03 发布协调完整 Release 回归：Hub43 + Runtime134 + ASP.NET9 + Cli5 = 191 项通过，0 失败/0 跳过，`artifacts/acceptance/p5-03-publication-full/`。

P5-03 发布协调增量：新增 `ApplyAndPublishRevocationsAsync` 和 `RetryRevocationPublicationAsync`，接受有效撤销后全局阻断，检查点持久化与独立可信发布回调成功后才解除；失败/取消保持阻断，旧纯内存入口不能绕过。5 项新增案例、策略专项共36项通过，`artifacts/acceptance/p5-03-publication/`。实际防回退存储适配、发布事务进程故障及生产接线仍待验，P5-03 保持 IN_PROGRESS，详见 `docs/certification-revocation.md`。

真实 Worker 环境追溯增量：`artifacts/worker-certification/2415ed1585e3442e9b02b71f751f265d/report.json` 绑定 `environment.json`，修正独立依赖层陈旧重复元数据后，模块版本/安装依赖约束检查与普通/流式/新会话故障恢复再次通过。记录解释器、关键模块、包元数据及原源码 2,853 个 Python 文件摘要；6 项环境探针测试通过。旧元数据可从 `artifacts/worker-dependency-quarantine/` 恢复，金标准环境不变；不是完整生产锁/SBOM或漏洞扫描。

真实 Worker 流式/恢复增量通过：`artifacts/worker-certification/74418e49cd6442da81e814b0e4d26ac9/report.json`。固定 DistilBERT 普通、分类流式 8 条数据+唯一结束事件、会话卸载后 Worker 强制终止/新会话自动恢复均通过标签/概率门槛；最终旧/新自有进程均退出。不是 token 增量生成、执行中重放或生产容器认证；工具首次 macOS 退出码读取失败报告保留，见 `docs/python-worker-real-validation.md`。生产代码未改。

新增 Worker 认证工具后的完整 Release 回归：Hub43 + Runtime129 + ASP.NET9 + Cli5 = 186 项通过，0 失败/0 跳过，`artifacts/acceptance/worker-real/`。此测试总数不包含单独运行的真实模型认证工具，模型证据单列如下。

2026-09-06 真实 Worker gRPC：新增解决方案内 `ModelScope.Net.WorkerCertification` 工具；固定 DistilBERT 五制品/gold 校验、.NET 子进程管理、真实 gRPC 调用、卸载健康及进程清理通过。8/8 标签一致，最大概率误差 1.1921e-7，报告 `artifacts/worker-certification/f04a8f0c118c4ad0a639208ba57b8906/report.json`。实际 MPS，不是 CPU/生产容器认证；未比较 logits，真实模型流式/恢复、环境完整锁及其他任务仍待验。下面准备性记录保留为历史。

2026-09-06 恢复复验：当前 p5-current-local 镜像 12 项本机 ARM64 隔离检查通过，报告 `artifacts/container-checks/870c46434797409aa3c9b6edbc1d65a7/report.json`；部署安全与真实 mTLS 专项 6 项通过、0 跳过，`artifacts/acceptance/docker-resumed/`。当前镜像漏洞扫描被自动安全审核拒绝，可能向 Docker Scout 外部服务提交依赖元数据，待用户明确授权；不能复用旧镜像零漏洞结论。P4-07 仍待目标平台与长稳验收。独立 Python Worker 依赖冲突已修正，原 pipeline 导入及直接后端 DistilBERT 8 条标签对照通过（实际 MPS）；.NET gRPC 真实模型链路、数值容差与完整认证仍待完成，见 `docs/python-worker-real-validation.md`。

| 验证项 | 结果 | 证据 |
|---|---|---|
| Release 编译 | 通过，含 Embedding、文本分类、图像分类、GGUF Header、llama.cpp 运行时认证、性能基准、Worker 隔离、模型资源治理及 P4-05 部署基线，0 警告、0 错误 | `dotnet build ModelScope.Net.sln -c Release --no-restore` |
| 自动化测试 | 通过，Hub18 + Runtime105 + ASP.NET Core9 + Cli5，共137项，0失败/0跳过；生产策略真实BGE演练通过 | artifacts/acceptance/p2-04-final/；后续缓存修复完整Runtime105项见artifacts/acceptance/p2-04-cache-final/ |
| 模型资源治理 | 通过，12 项资源池专项测试；预热复用、LRU、活跃/待授予保护、全局/单模型并发、有限队列、超时、估算内存、空闲清理、失败重试和脱敏快照全绿 | `ModelSessionPoolTests`；`modelscope-resource-pool` 健康检查 |
| P4-05 部署安全 | 通过，Linux CPU/GPU Kustomize 正常渲染；5 项部署控制机器校验通过；真实 Python gRPC mTLS 接受受信客户端并拒绝匿名客户端 | `WorkerSecurityEvidenceTests`；`RealWorker_MutualTlsAcceptsClientCertificateAndRejectsAnonymousClient`；`deployment/p4-05-deployment-profile.json` |
| P4-05 容器镜像 | 通过，本机 Linux/ARM64 协议基础镜像使用固定 Alpine digest；uid 65532、只读根/模型卷、无网络、0.5 CPU、256 MiB、64 PID 和限额临时目录实测生效；Docker Scout 43 包全等级 0 漏洞 | `deployment/container-build-evidence.json`；`modelscope-net/python-worker:p4-05-local` |
| NuGet 漏洞扫描 | 通过，所有项目的直接与传递依赖均无已知漏洞 | `dotnet list ModelScope.Net.sln package --vulnerable --include-transitive` |
| 在线元数据 | 通过 | `info Qwen/Qwen2.5-0.5B-Instruct` |
| 固定 Commit 下载 | 通过，SHA-256 校验成功 | `download ... --revision 186d8559... --allow config.json` |
| BGE 固定 Revision 快照 | ONNX、SafeTensors、Tokenizer 共 11 个文件下载并校验；Inspector 可恢复真实目标大小 | BAAI/bge-small-en-v1.5@160f4d645...；manifest SHA-256；ModelInspectorTests |
| DistilBERT 固定 Revision 快照 | ONNX、SafeTensors、配置和 WordPiece 共 5 个文件下载并校验 | distilbert-base-uncased-finetuned-sst-2-english@ef2f51c8...；manifest SHA-256 |
| MobileNet V2 固定 Revision 快照 | ONNX 与 PyTorch 源模型各 3 个文件下载并校验 | mobilenet_v2_1.0_224-ONNX@ba6621a...；google/mobilenet_v2_1.0_224@4d4b642...；双 manifest SHA-256 |
| Qwen2.5 GGUF 固定 Revision 快照 | Q2_K 文件完整下载并校验，415182688 字节 | Qwen/Qwen2.5-0.5B-Instruct-GGUF@2e50b77...；SHA-256 `9ee36184...98cb8` |
| 离线缓存复用 | 通过，未访问网络且重新校验文件 | 同一路径加 `--local-files-only` |
| 能力检测 | 通过，Model ID/Commit/架构可恢复 | `inspect artifacts/live-smoke-fixed/Qwen2.5-0.5B-Instruct` |
| Remote 调用契约 | 非流式与 SSE 模拟契约通过 | RemoteRuntimeTests |
| Remote 在线调用 | 通过，非流式返回 `OK`，SSE 流式收到终止事件并正常退出 | `run Qwen/Qwen3.5-35B-A3B`；Token 仅以单进程环境变量注入且未落盘 |
| 多进程缓存 | 通过，两个独立 CLI 进程同时下载同一固定快照 | `artifacts/multiprocess-smoke`；cache verify 无问题 |
| CLI 凭据 | 通过，原子写入、权限 0600、退出删除且输出无 Token | CredentialStoreTests；login/logout 冒烟 |
| Python Worker 客户端 | HTTP/SSE 兼容传输与 gRPC 主传输的健康、加载、调用、流式和释放通过 | PythonWorkerRuntimeTests；GrpcPythonWorkerRuntimeTests |
| Python Worker 进程 | 真实 gRPC 子进程启动、外部崩溃后自动拉起、主动重启和进程树清理通过 | GrpcPythonWorkerRuntimeTests；LocalPythonWorkerSupervisorTests |
| ONNX CPU 执行 | 有效 Add 图完成 float 张量推理；形状、缺失输入、损坏模型错误通过 | OnnxRuntimeAdapterTests |
| ONNX Embedding | BGE-small-en-v1.5 真实模型 Python/.NET 对照通过：8×384，最大绝对误差 2.30e-7，最低余弦 0.999999999999 | `tests/compatibility/bge-small-en-v1.5/certification-report.json`；OnnxEmbeddingRuntimeTests |
| ONNX Mean Pooling Embedding | all-MiniLM-L6-v2 固定 Revision 的 Python/.NET 对照通过：8×384，最大绝对误差 2.09e-7，最低余弦 0.999999999999 | `tests/compatibility/all-minilm-l6-v2/certification-report.json`；CertificationEvidenceTests |
| ONNX 文本分类 | DistilBERT SST-2 真实模型 Python/.NET 对照通过：8 样本标签 100% 一致，最大 logits 误差 3.48e-6、概率误差 2.87e-7 | `tests/compatibility/distilbert-sst2/certification-report.json`；OnnxTextClassificationRuntimeTests |
| ONNX 图像分类 | MobileNet V2 真实模型 Python/.NET 对照通过：3 样本、1001 类，Top-1 与最低 Top-5 均 100%，最大 logits 误差 0.2061、概率误差 0.004856 | `tests/compatibility/mobilenet-v2-image-classification/certification-report.json`；OnnxImageClassificationRuntimeTests |
| GGUF Header 白名单 | Qwen2.5 0.5B Q2_K 真实文件通过完整 SHA-256 和 v3/qwen2/Q2_K/gpt2/Chat Template 白名单；Header 报告独立标记需要实际加载，后续运行时报告已满足该要求 | `tests/compatibility/qwen2.5-0.5b-instruct-gguf/certification-report.json`；GgufHeaderTests |
| llama.cpp 固定分发 | b10516/commit b95502ba9 的 macOS ARM64 官方包与执行文件校验通过；Ubuntu/Windows 官方包已固定 SHA-256、尚未实机执行 | `deployment/llama.cpp-b10516.json`；运行时认证报告 |
| GGUF 实际运行 | macOS ARM64/CPU 实际加载 Qwen2.5 0.5B Q2_K；非流式、SSE、外部崩溃后新进程恢复、宿主停止清理均通过；服务仅绑定回环并使用临时随机 API Key | `tests/compatibility/qwen2.5-0.5b-instruct-gguf/runtime-certification-report.json`；GgufRuntimeTests |
| Linux 图像运行时资产 | linux-x64 framework-dependent publish 含 `libSkiaSharp.so`；SkiaSharp 依赖漏洞扫描无命中 | `dotnet publish tools/ModelScope.Net.VisionCertification -r linux-x64 --self-contained false`；`dotnet list package --vulnerable` |
| ASP.NET Core | DI、健康检查、HostedService 启停通过 | ModelScope.Net.AspNetCore.Tests |
| 签名 Catalog | RSA-PSS 验签、篡改拒绝、移动分支拒绝通过 | CompatibilityCatalogTests |
| 发布清单认证门禁 | 5 个固定 Revision 本地模型、必选任务覆盖、认证报告回链与 `passed` 状态均通过 | ReleaseManifestCertificationTests |
| P3-08 CPU 性能门禁 | macOS ARM64/CPU 两轮重复基准的 5 模型、9 场景全绿；证据固定 Revision、环境、阈值和保守聚合规则；GPU 与未测平台保持 fail-closed | `tests/performance/p3-08-cpu-macos-arm64.json`；PerformanceEvidenceTests；`docs/performance-capacity.md`；可检查 HTML 报告 |
| OpenTelemetry 与审计 | 推理成功、失败、流式终止/提前退出、信号禁用和审计落点故障均通过；请求正文、参数、输出、异常消息、私有 Model ID 与路径未进入 trace、metric 或 audit | ModelScopeTelemetryTests；`docs/telemetry-audit.md` |
| Worker 应用层隔离 | 真实 Python/llama 子进程未继承父进程秘密；无 Key、越界模型目录、超限请求/会话、非回环绑定、远程明文 Endpoint 和敏感显式环境均被拒绝；Key 文件 0600 且释放删除 | WorkerProcessSecurityPolicyTests；LocalPythonWorkerSupervisorTests；GrpcPythonWorkerRuntimeTests；GgufRuntimeTests；`deployment/worker-security-matrix.json` |

## 完成验收检查

1. 所有目标发布范围内任务为 COMPLETED 或有批准的 DEFERRED 决策。
2. release-manifest-v1.yaml 已批准并与 Catalog 一致。
3. dotnet test、在线契约测试、模型金标准和 72 小时稳定性测试全部通过。
4. 安全、许可证、灰度和回滚证据齐全。
5. 产品、技术、安全负责人完成 Go/No-Go。

### 2026-09-08 P5-04 语音识别本机认证

新增SpeechRecognitionRequest/Result及会话扩展；Worker只接受内联`audio/wav`，拒绝路径、URL、未知字段和额外parameters，限定单声道、16kHz、PCM16、20MiB及最长10分钟，并把受控WAV解码为浮点波形。原项目FunASR真实输出为单元素列表，本适配器规范化为稳定`text`对象；5项Python传输测试及4项.NET契约测试通过。固定模型`iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-pytorch@ff922d0e9af830cee4d1ec9b57b193196941efd8`，模型权重880502012字节、SHA-256 `5bba782a5e9196166233b9ab12ba04cadff9ef9212b4ff6153ed9290ff679025`，官方样本SHA-256 `a1bd32dc78493c123f9625a66deee562aed2895f53fbc39f2cca3be7e6f4f20f`。

Python金标准`artifacts/asr/python-runs/3aac7830b82d4aa094f5218a2fa7e120/report.json`与.NET报告`artifacts/asr/dotnet-runs/b44910dd6e5d4d5c92e53d7a93ca7281/report.json`均为passed，普通和流式精确得到“欢迎大家来体验达摩院推出的语音识别模型”，流式恰好一个data及一个done；Worker PID 97035终止后以97038恢复并再次精确匹配。FunASR更新检查已禁用，固定快照离线加载。Release完整.NET回归251项通过、0跳过（Hub43/Runtime194/AspNetCore9/CLI5），TRX在`artifacts/acceptance/p5-04-asr`；Worker全部30项Python测试通过。范围仅本机macOS CPU、一个中文模型与一个官方样本，不声明生产容器/GPU/跨平台、长音频质量、许可证业务审批或全模型覆盖；语音识别本机迁移认证完成，按顺序进入ONNX文本生成。

### 2026-09-08 P5-04 OCR本机认证

新增OcrImageInput、OcrRecognitionRequest/Result、OcrDetectionRequest/Result及会话扩展；Worker只接受单个内联PNG/JPEG，拒绝路径、URL、未知字段和额外parameters，限制8MiB、1600万像素、8192边长、文本及多边形输出规模。真实模型揭示单图识别的text实际为单元素数组，与源码注释的字符串不一致，规范化契约按真实行为保留集合并提供Text便捷属性。6项Python OCR传输测试、5项.NET契约测试通过。

通过.NET Hub下载并固定识别模型damo/cv_convnextTiny_ocr-recognition-general_damo@6346468feced662993f8a79f7f62004dc534cca6及检测模型damo/cv_resnet18_ocr-detection-db-line-level_damo@3a6b98fc046f99e8ec97d4ed25f478909a123253；输入由固定模型卡资源派生，证据在artifacts/ocr/inputs。原Python金标准为artifacts/ocr/python-runs/42b3fe987c334b36bdc6c980f8510c3d：识别“电子元器件提供BOM配单”，检测53个多边形。最终.NET认证artifacts/ocr/dotnet-runs/df4bd3e025e4483cb5fc1e78457b1b7b/report.json状态passed：普通及流式结果精确一致、各恰好一个data和一个done；主动终止PID94358后由PID94364恢复并成功检测；自动鉴权保持启用。三个早期Unavailable失败和一次显式预启动后Unauthenticated证据保留，根因是认证工具构造GrpcPythonWorkerRuntime时漏传supervisor，并非模型结果失败；最终工具已修正。

GrpcPythonWorkerRuntime同时修复慢启动健康探测：健康端点尚不可用时调用EnsureRunning保留仍活跃进程，仅实际RPC恢复错误强制Restart；定向单元测试通过。Release完整.NET回归247项通过、0跳过（Hub43/Runtime190/AspNetCore9/CLI5），TRX在artifacts/acceptance/p5-04-ocr；Worker全部25项Python测试通过。范围仍是本机macOS CPU与各一组识别/检测样本，不声明模型质量、许可证审批、容器/GPU/跨平台通过。OCR本机迁移认证完成，按顺序进入语音识别。

### 2026-09-08 P5-04 图像生成本机认证汇总

认证汇总 artifacts/image-generation/certification-runs/85f5ef8defef48249f3c2db9eef754de/report.json 状态passed。固定AI-ModelScope/stable-diffusion-v1-5提交50b8f071f400a9a501706bb7530481056f5f0558；red-cube、blue-vase、green-toy-car三个512×512样本的原Python与.NET解码RGB逐通道完全一致，differentChannels均0。首样本普通、流式、终止事件、同种子重复、Worker被终止后新PID恢复均通过。汇总绑定四个safetensors权重大小/SHA256、组合环境报告及7个关键源码/计划文件哈希。范围仅本机macOS CPU、单模型固定依赖；不宣称GPU、容器、跨平台、模型质量或许可证审批通过。图像生成功能的本机迁移认证完成，生产矩阵仍留待最终门禁；按用户顺序进入OCR。

### 2026-09-07 P5-04 真实流式验证启动

ImageSmoke新增同会话普通/流式双次真实推理，要求恰好一个data和一个done/null终止事件、无终止后事件，并比较同种子PNG编码字节，保存stream.png与事件计数。运行目录 artifacts/image-generation/dotnet-runs/32e68a7f756d41b99621d177d2c42fb8，启动会话50268；记录时普通调用已产出dotnet.png、流式仍运行，尚不判通过。同步acceptance-audit.md的P5-04状态，避免仍标记“尚未实现”。

### 2026-09-07 P5-04 首个端到端像素一致样本

上述.NET运行会话21570已exit0。compare_image_pixels.py对Python样本3907b323f70d44fa8fe37ee8dcc812bb与.NET样本1b9db6e0674d49e7a0f52c0cfb7f51f6的PNG进行独立解码，512×512 RGB像素完全一致，differentChannels=0，双方RGB SHA256均为956cba823cddbcb4e60b95e16eb5c266b7946d03d6bbdf5b997fef26e4f7f80a。证明该本机/依赖/固定输入单次普通调用链一致；不替代多样本、重复运行、流式、恢复及完整证据绑定认证，P5-04仍IN_PROGRESS。

### 2026-09-07 P5-04 .NET真实调用工具

新增并纳入解决方案 tools/ModelScope.Net.ImageSmoke，使用ModelInspector读取真实快照、启动单会话Worker并通过GenerateImagesAsync生成PNG，保留模型版本与响应耗时、finally停止Worker；新增tools/compare_image_pixels.py独立解码两侧PNG并进行精确RGB像素比较。初次工具手工空Artifacts被运行时拒绝（a629bf7f5ae54f519fb343c47dd6c058，failed证据保留）；改用真实检查器后已启动运行，目录 artifacts/image-generation/dotnet-runs/1b9db6e0674d49e7a0f52c0cfb7f51f6，记录时会话21570仍活跃，未判定通过。此工具是单样本冒烟，不具备完整认证所需全部输入/代码哈希绑定和恢复/流式测试。

### 2026-09-07 P5-04 正向Python样本与回归

保留安全检查器，将同一红色木块提示词调整为512×512、30步、seed42。真实CPU运行exit0，得到非纯色输出，像素范围0–253，耗时约58秒；证据为 artifacts/image-generation/python-runs/3907b323f70d44fa8fe37ee8dcc812bb/report.json 及 python.png。BGR像素SHA256为2d92c5a0f75e9817540df306d5bca3166236ed99539dde4b3d0731a27abef27f。usableForParity仅表示通过非纯色检查，不是模型质量、内容安全或跨环境确定性认证。Release完整.NET回归241项通过、0跳过（Hub43/Runtime184/AspNetCore9/CLI5），TRX在 artifacts/acceptance/p5-04-image-adapter/。下一步使用此固定输入执行.NET Worker调用及解码后的像素比对，当前尚未证明.NET与Python一致。

### 2026-09-07 P5-04 首次真实Python推理

下载会话83289已exit0、17/17完成；四个权重大小与SHA256全部匹配固定提交。原Python管线通过工具 tools/sd15_python_smoke.py 在CPU加载并执行256×256/10步/seed42请求，证据目录 artifacts/image-generation/python-runs/b75e6271ffc445b0a2101840bdbf3f82。安全检查器提示替换为黑图，不能用于正向图像金标准认证。历史report的passed仅代表当时冒烟执行与形状检查，不代表有效图像或.NET认证，保留原报告不回改。后续脚本增加像素范围和usableForParity字段，纯色输出判失败，不关闭安全检查器。独立依赖层补齐后元数据约束复核passed、无错误。尚待获得有效正向样本、.NET端到端比对及完整认证证据。

### 2026-09-07 P5-04 图像依赖约束复核

下载会话83289复查仍活跃，16/17文件完成，UNet仍传输，未重启。组合环境元数据审计发现diffusers声明的importlib_metadata缺失；独立图像层补装importlib-metadata8.7.1和zipp3.23.0，并更新版本清单。新增 tools/verify_sd15_weights.py，以固定提交仓库API返回的四组大小/SHA256核验权重，预期值不从本地权重生成；该工具只核验四个权重，不覆盖配置、许可证或推理认证。下载完成后方可据执行结果记录完整性通过。

### 2026-09-07 P5-04 图像依赖预检

复查下载会话83289仍在运行，未重复启动。新增独立.image-model-deps，安装diffusers0.35.2、torchvision0.24.1、opencv-python-headless4.13.0.92，使用--no-deps保留原.certification-venv及.worker-model-deps。版本清单见worker/python/image-validation-requirements.txt；不是完整依赖锁或SBOM。安装期间OpenCV下载超时后由pip续传成功。组合PYTHONPATH下实际导入torch2.9.1、torchvision0.24.1、cv2 4.13.0、DiffusionPipeline和原项目modelscope.pipelines成功（exit0）；尚未加载模型，不证明全部依赖约束一致或推理成功。此预检生成了默认ModelScope AST缓存；后续认证需使用显式隔离缓存路径并记录环境来源。权重下载完成与哈希核验尚待确认。

### 2026-09-07 P5-04 安全权重加载与下载

Worker为text-to-image-synthesis显式传入use_safetensors=True，不实现失败后pickle重试；其他任务参数保持不变。13项Python测试通过，新增构造参数隔离测试。该保证限于已核对的原Diffusers封装，不能证明任意第三方管线都会遵守同名参数，也不替代远程代码隔离。通过固定提交仓库文件API确认四个必需组件均有safetensors（UNet 3438167540、VAE 334643276、文本编码器492265874、安全检查器1215981830字节）。已启动.NET CLI按白名单下载17个文件至 artifacts/image-generation/sd15-snapshot，提交仍为50b8f071f400a9a501706bb7530481056f5f0558；启动时下载尚在运行，不视为完成。恢复时先检查现有下载进程/会话及完整性，再决定是否续传，不能因本记录存在就认定进程仍活跃。真实加载与认证尚未执行。

### 2026-09-07 P5-04 真实图像模型预检

通过现有.NET CLI将 AI-ModelScope/stable-diffusion-v1-5 的 v1.0.0 解析为提交 50b8f071f400a9a501706bb7530481056f5f0558，并按固定提交下载8个配置/README文件到 artifacts/image-generation/sd15-metadata。官方模型卡：https://www.modelscope.cn/AI-ModelScope/stable-diffusion-v1-5 。本机实测64GiB内存、约426GiB可用磁盘。下载内容确认任务text-to-image-synthesis、管线diffusers-stable-diffusion、StableDiffusionPipeline及PNDMScheduler，包含安全检查器配置。README声明CreativeML OpenRAIL-M，未下载到独立LICENSE文件，不能据此视为许可证审核完成。原项目加载器默认float32、非CUDA设备走CPU、默认use_safetensors=False；现有Worker尚无加载期safetensors选项，因此权重选择与安全加载需先解决，不能仅下载safetensors后声称可加载。当前仅完成元数据预检，未下载权重、未运行真实图像推理，认证状态不变。

### 2026-09-07 P5-04 类型化输出增量

新增 ImageGenerationResult.FromResponse 和会话扩展 GenerateImagesAsync：返回PNG字节与尺寸，并保留原ModelResponse的模型、版本、运行时及耗时。解析器限制最多4张、合计4Mi像素和2MiB PNG字节，校验Base64长度、PNG签名、首块IHDR及声明尺寸一致性；异常转换为InferenceFailed且不回显响应内容。该检查不是完整PNG解码器，不验证全部块CRC、压缩像素或内容安全，不能用作不可信图片的安全认证。6项.NET图像定向测试通过（4项请求、2项输出）；本轮未重跑完整回归。此前“类型化输出未完成”已推进为初版实现，真实模型调用、端到端图像解码比对、模型级确定性和容器重认证仍待完成，P5-04保持IN_PROGRESS。

### 2026-09-07 P5-04 种子输入增量

图像生成请求新增可选 Seed（0至Int64.MaxValue）。Worker在完成参数校验后，为每次请求创建独立CPU torch.Generator，将seed转换为原ModelScope接受的generator，不设置进程级随机种子；未提供seed时保留模型默认随机行为。12项Python测试通过，包括实际torch随机序列重复与全局RNG状态不变测试；4项.NET图像请求定向测试通过。最近完整.NET回归仍为种子增量前238项，未用定向测试推算完整结果。原P5-04记录中的“种子未完成”由本增量替代为“种子输入已实现，模型级确定性未认证”。不同设备、模型和依赖版本是否重复输出必须通过真实模型认证，不能由随机生成器测试证明。类型化输出、真实模型认证和新容器验证仍待完成，P5-04保持IN_PROGRESS。
