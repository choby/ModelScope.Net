# 迁移验收审计台账

日期：2026-09-08。结论：生产1.0最终验收尚未通过；本机技术预览工程候选通过。原项目基线：53f61360c8c10a31c7adae483c223188bb602948。最终源代码/方案差异和NO-GO关闭条件见`docs/final-acceptance-2026-09-08.md`。下表反映当前状态，后续证据入口保留历次检查点；历史测试数和当时缺口不替代本表。

范围依据技术方案 FR-01～FR-10 和迁移计划：模型下载、多运行时调用与运维。原项目训练、微调、数据集、上传管理和完整 Python 模型代码重写不属于既定范围。本文件是实际代码初审记录，剩余任务完成后仍须全量复核。

## 功能核对

| 要求 | 原项目入口 | .NET 入口及当前证据 |
|---|---|---|
| FR-01 查询与版本 | hub/api.py及委托的modelscope_hub兼容层 | GetModelRevisionsAsync/ResolveModelRevisionAsync、固定Commit/续传隔离/缓存分代实现，当前Hub43项通过；真实分支、附注标签及四进程共享缓存/离线访问通过；业务失败/损坏清单/正文脱敏已补验。真实私库、分支换代压力及未覆盖协议行为仍待验；docs/hub-revisions.md |
| FR-02 下载与过滤 | hub/file_download.py、snapshot_download.py | DownloadFileAsync/DownloadSnapshotAsync；本轮补离线过滤与身份校验 |
| FR-03 鉴权 | hub/api.py、errors.py | 凭据/模拟鉴权有证据；真实私有/受限授权下载仍缺 |
| FR-04 下载可靠性 | hub/file_download.py、callback.py | 续传、Hash、取消、并发有测试；连续7天至少2000次、99.5%契约SLO未执行 |
| FR-05 缓存与离线 | hub/cache_manager.py、utils/caching.py | Blob/Manifest、多进程已有证据；本轮发现并修复指定本地目录可返回错误模型/Revision |
| FR-06 检测诊断 | pipelines/builder.py、模型配置 | ModelInspector已支持配置/README简单许可证、requirements简单约束、制品体积、保守架构任务推断、元数据和目录扫描限额。当前全套235项通过；复杂依赖/SPDX、通用部署环境匹配及真实RAM/VRAM需求仍缺。Worker环境探针仅覆盖专用验证环境，不代替通用诊断；docs/model-diagnostics.md |
| FR-07 策略与认证 | pipelines/builder.py、动态注册 | 本轮修复：生产策略验签、精确ID/Commit/任务/平台/运行时摘要及制品Hash；默认关闭自动Remote，显式允许后独立认证回退；会话与缓存任务边界通过 |
| FR-08 普通/流式推理 | pipelines/base.py、各任务 pipeline | 四类运行时与五模型技术认证有证据；新增真实.NET gRPC→ModelScope→DistilBERT普通/流式/新会话故障恢复，标签及概率对照通过，并绑定专用环境报告。仅macOS/MPS分类链路，非所有Python模型/任务或生产容器认证；docs/python-worker-real-validation.md |
| FR-09 版本与回滚 | 模型 Revision、Python 依赖 | 生产路由/灰度精确绑定，真实BGE灰度/回滚、签名撤销、更新前持久暂存、启动对账、保存后及暂存后进程恢复、发布失败/丢失确认重试通过。本机进程级提交前窗口已有证据；生产防回退外部存储、自动分发、目标部署故障及Catalog换代仍待完成；docs/certification-revocation.md |
| FR-10 观测运维 | Python 日志与服务 | ASP.NET、Telemetry、资源池有测试；新增机器校验的 Prometheus 指标契约、Grafana Dashboard、失败率/延迟/审计丢失告警及接线手册。当前信号只覆盖推理调用，下载/排队/资源/Worker/额度指标、生产 exporter/接收人、批准 SLO、目标平台与72小时门禁仍缺；docs/observability-runbook.md |

## 尚未满足的最终门槛

| 门槛 | 状态和下一步 |
|---|---|
| P0 代表模型与金标准 | 工程草案已达到15个固定Revision：11个实测模型及4个在线元数据核验候选；远程多模态、trust_remote_code、GGUF Embedding、ONNX目标检测均有候选，后两项按当前实现明确Unsupported。业务未批准集合，真实私有/受限仓库仍缺，多个许可证及4个候选的运行/金标准未完成，不能关闭P0-04/05或发布 |
| P0 平台/安全/许可证/批准 | 平台验证矩阵、威胁模型及许可证流程已形成；GPU组合/目标主机和真实负责人批准仍缺；MobileNet许可证未关闭 |
| P1 私有/受限 | 使用真实获授权仓库与安全凭据执行授权/未授权对照 |
| P2 通用生产路由 | 已实现范围内的签名路由、会话/缓存边界及真实BGE复验通过；撤销存储协调已关闭本机进程级提交前窗口，但生产外部信任根和分发控制面未认证，不据此宣称全局安全审计完成 |
| P3 数值与性能 | 既有技术证据需最终复验，macOS结果不得代表其他平台 |
| P4-06 | 本地工程演练通过；集群级演练归P4-07 |
| P4-07 | Windows/Ubuntu x64、冻结GPU组合、CNI/PID/证书及实际72小时测量未完成 |
| P4-08 | No-Go：前置门槛与真实三方批准未完成 |
| P5 | P5-01本机五模型复认证及负面测试通过，远程CI待验；P5-02本机真实上游检查/固定模型复认证有证据，持续远程执行待验；P5-03见FR-09；P5-04图像生成、OCR识别/检测、Paraformer语音识别及SmolLM ONNX贪心文本生成已完成固定模型本机CPU认证，生产环境矩阵与更多模型仍待验；P5-05按原计划1.0后评估 |
| 最终核对验收 | 已于2026-09-08完成源代码/方案逐项复核：本机技术预览工程候选通过，生产1.0为NO-GO。关闭条件为真实私库、批准清单/许可证/SLO/平台、生产可信存储与分发、远程CI/镜像供应链、7天Hub及72小时稳定性证据和三方签字；测试数量不代替范围证明 |

## 证据入口

- P4分发与升级工程基线：8个库项目统一`0.1.0-preview.1`，解决方案pack只产出预期nupkg/snupkg；验证器检查元数据、net10.0程序集、安全路径、独立符号和内部依赖精确对齐，PackageSmoke只从本地包源编译顶层ASP.NET Core公共入口。`docs/distribution.md`和`docs/upgrading.md`补齐生成、灰度、回退及安全状态不可降级规则。这不是nuget.org发布、签名/provenance、干净目标runner或1.0验收。
- P4可观测性工程基线：`deployment/observability`包含实际Meter名称绑定的机器契约、Grafana Dashboard和Prometheus告警；2项测试约束四个指标、有限标签、全部查询及Critical审计写失败。默认门槛不是批准SLO，未覆盖的下载/资源/Worker等信号继续列为FR-10缺口。

- P5-03更新前暂存与启动对账：`IRevocationPublicationStore`路径在签名验证后立即阻断、持久暂存后才接受，提交响应丢失可幂等重试；4项新增测试及完整Release 258项通过（Hub43/Runtime201/ASP.NET9/CLI5），TRX在`artifacts/acceptance/p5-03-revocation-store/`。真实BGE报告`artifacts/rollout/revocation-store-crash-547949e27cbe4ac4bc33214832de86ef/crash-report.json`记录写进程暂存后exit -9、新进程自动对账、撤销阻断和旧检查点拒绝。`FileRevocationPublicationStore`不抗管理员整体回退，不替代生产外部信任根/目标部署测试。
- P5-04 ONNX文本生成本机认证：固定`onnx-community/SmolLM-135M-ONNX@cde45563cc98f8fa03cbdf2c074985aec9efb3e5`与量化图SHA-256 `7ae6828aa72763890cfc729cc0a84adeaa848bc0cba834a2aedbbcc02681936e`。Python金标准`artifacts/onnx-text-generation/python-runs/6608d1c7c8f74fc2a88b50776d6ebb8b/report.json`与.NET报告`artifacts/onnx-text-generation/dotnet-runs/376c39fa4fe54ec6a06478c697d04965/report.json`在3个提示词×8个贪心token上ID和文本精确一致，重复确定，流式8 data+1 done；普通CLI payload、`--prompt`及流式路径通过。完整Release回归254项通过（Hub43/Runtime197/ASP.NET9/CLI5），TRX在`artifacts/acceptance/p5-04-onnx-text-generation/`。仅证明本机macOS ARM64 CPU、batch1、固定图短上下文，不替代生产平台/性能/更多模型认证；详见`docs/onnx-text-generation.md`。
- P5-04语音识别本机认证：固定`iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-pytorch@ff922d0e9af830cee4d1ec9b57b193196941efd8`及官方16kHz PCM16单声道WAV；Python金标准`artifacts/asr/python-runs/3aac7830b82d4aa094f5218a2fa7e120/report.json`与.NET普通/流式/进程恢复报告`artifacts/asr/dotnet-runs/b44910dd6e5d4d5c92e53d7a93ca7281/report.json`精确文本一致，PID 97035→97038。完整.NET回归251项、Worker 30项均通过；只证明本机macOS CPU、单模型单样本，不替代生产平台/容器/质量矩阵。
- FR-06 保守任务推断与CLI衔接：8项新增案例，完整235项通过、0跳过（Hub43/Runtime178/ASP.NET9/Cli5），artifacts/acceptance/fr06-task-inference/；真实固定DistilBERT未指定任务/运行时，自动调用onnx-text-classification返回POSITIVE。未知/冲突架构不猜测，推断有警告且不提升认证等级；单条冒烟不是全量数值认证。
- FR-06 目录扫描增量：6项新增案例、完整227项通过（Hub43/Runtime170/ASP.NET9/Cli5），0跳过，artifacts/acceptance/fr06-scan-limits/。条目/深度/取消、忽略目录剪枝、目录链接循环与文件链接兼容已覆盖，真实 DistilBERT inspect 通过；不泛化为OS沙箱、所有读取入口限额或推理重认证。
- 2026-09-07 核心元数据限额修复：10项新增案例，完整221项通过、0跳过（Hub43/Runtime164/ASP.NET9/Cli5），artifacts/acceptance/fr06-metadata-final/。配置1 MiB/清单16 MiB；边界大小允许、超限拒绝，损坏或非对象配置保守标记远程代码未知，解析警告无原始异常。全目录数量/其他读取入口仍需分别审计，不泛化为全部资源治理完成。
- 2026-09-07 FR-06 README 顶层简单许可证识别增量：19个新增案例，完整211项通过、0跳过（Hub43/Runtime154/ASP.NET9/Cli5），artifacts/acceptance/fr06-readme-keys/。来源行号和人工审核标记保留；重复键及不支持结构拒绝部分结论，不执行、不回显敏感原文。仅限定子集，不代表通用YAML有效性或SPDX/法律审批完成。
- P5-03 并发更新串行化/等待者取消回归通过；完整192项通过、0跳过（Hub43/Runtime135/ASP.NET9/Cli5），artifacts/acceptance/p5-03-publication-serialized/。
- P5-03 真实 BGE 发布异常/重试演练通过：artifacts/rollout/p5-03-publication-bge/report.json，未撤销模型的已加载/新会话在发布未确认时被阻断，确认后恢复金标准。仅进程内异常注入；接受更新后至可信锚提交前的进程中断窗口未被证明安全，需真实外部存储发布意图/启动对账协议和故障演练，不能关闭 P5-03。
- P5-03 发布协调完整 Release 回归 191 项通过，0 跳过（Hub43/Runtime134/ASP.NET9/Cli5），artifacts/acceptance/p5-03-publication-full/。
- P5-03 撤销持久发布协调：5项新增案例、策略专项共36项通过，artifacts/acceptance/p5-03-publication/。有效签名更新后阻断授权，保存并刷新新检查点后回调可信发布，成功才解除；失败/取消/不确定保持阻断，可新代重试。回调为测试替身，不证明真实防回退控制面，生产接线与真实事务故障仍待完成。
- Worker 环境追溯与再认证：artifacts/worker-certification/2415ed1585e3442e9b02b71f751f265d/report.json 哈希绑定 environment.json，实际模块版本一致、重复元数据为零、已启用依赖约束错误为零；记录解释器/关键模块/安装包 METADATA 和 2,853 个原源码 Python 文件摘要。普通/流式/新会话恢复再验通过，6 项环境探针测试通过。依赖 wheel 完整锁、非 Python 资源、完整包内容哈希及扫描仍未完成，不能扩大认证范围。
- 真实 Worker 流式/新会话故障恢复通过：artifacts/worker-certification/74418e49cd6442da81e814b0e4d26ac9/report.json。普通、流式、重启恢复后真实 DistilBERT 均达到 8/8 标签、概率误差 <=1e-4；分类结果整批完成后逐条发送，非 token 级生成。故障发生在会话卸载后，不覆盖在途请求重放。最终清理通过，失败演练报告保留；其他平台、生产依赖和容器认证仍待完成。
- 新增 Worker 验证工具后完整回归 186 项通过（Hub43/Runtime129/ASP.NET9/Cli5），0 跳过，artifacts/acceptance/worker-real/。工具已加入 ModelScope.Net.sln；其真实模型执行证据独立于单元/集成测试总数，不据此关闭其他迁移与发布门槛。
- 真实 .NET gRPC Worker 首轮通过：artifacts/worker-certification/f04a8f0c118c4ad0a639208ba57b8906/report.json。固定 DistilBERT 8/8 标签一致，最大概率误差 1.1921e-7，真实会话卸载后健康和子进程退出通过；MPS、概率输出范围，不包含 logits、流式恢复、生产容器或所有模型。新工具及失败预检记录见 docs/python-worker-real-validation.md。
- 2026-09-06 Docker 再次复验：当前镜像 12 项本机 ARM64 隔离检查通过，artifacts/container-checks/870c46434797409aa3c9b6edbc1d65a7/report.json；部署安全及真实 mTLS 专项 6 项通过，artifacts/acceptance/docker-resumed/。新镜像漏洞扫描待用户明确授权外部依赖元数据传送，未执行。Worker 独立依赖层 Hub 版本已修正至 0.36.2，原 pipeline 导入成功；直接 Python 后端在 MPS 上 DistilBERT 8 条标签一致，仅准备性验证，不替代 .NET gRPC 链路、数值对照和最终认证。
- 真实Python Worker准备核对：协议环境缺模型依赖，金标准环境缺gRPC；已建立独立依赖层并固定附加依赖声明，原ModelScope源码组件索引生成991项。pipeline仍须完成datasets/Hub版本兼容检查，尚无真实Worker模型调用通过证据，见docs/python-worker-real-validation.md。
- FR-06诊断本轮：Hub43 + Runtime129 + ASP.NET9 + Cli5 =186项通过、0跳过，artifacts/acceptance/fr06-diagnostics/。许可证声明/文件证据、简单依赖约束、资源未知标记与非执行/超限行为有测试；不能代替环境兼容、许可证审批或资源剖析。
- 运行时错误正文后续修复：Remote/Python HTTP/GGUF普通及流式失败不再回显正文，Python gRPC不再输出Status.Detail或保留原始异常链。新增9项测试，完整Hub43 + Runtime127 + ASP.NET9 + Cli5 =184项通过、0跳过；artifacts/acceptance/runtime-transport-redaction/。范围见docs/security/runtime-error-redaction.md；下面175项检查点发现的传输错误正文问题已按此范围修复。
- Hub错误处理本轮：Hub43 + Runtime118 + ASP.NET9 + Cli5 =175项通过、0跳过，artifacts/acceptance/fr01-errors/。HTTP200业务失败/矛盾状态不再成功返回，损坏清单不再成为空列表，Hub异常不回显上游敏感正文。代码核对发现Remote/Python/GGUF运行时仍存在HTTP错误正文拼接，须在最终安全验收前分别处理，不能把本次Hub修复视为全局脱敏完成。
- 真实附注标签与四进程共享缓存通过：artifacts/hub-livechecks/73e6e9ae679d48458cbc005c19aa40a8/report.json。独立Git与.NET剥离Commit一致；四进程返回同一目录；不可达Endpoint下离线标签/Commit访问通过；只下载554字节配置，不算新增模型推理认证或长稳。
- FR-01缓存分代本轮：Hub33 + Runtime118 + ASP.NET9 + Cli5 =165项通过、0跳过，artifacts/acceptance/fr01-cache-generations-full/。旧快照内容保持、失败不发布新引用、离线新旧Commit访问、损坏指针拒绝及显式目录防覆盖均有测试；不代替多进程压力与掉电事务验收。
- FR-01入口统一：Hub31 + Runtime118 + ASP.NET9 + Cli5 =163项通过、0跳过，artifacts/acceptance/fr01-entrypoints/。独立单文件重试固定Commit、临时续传身份隔离及详情与文件列表一致性已覆盖；引用缓存换代仍未实现。
- FR-01固定Commit本轮：Hub29 + Runtime118 + ASP.NET9 + Cli5 =161项通过、0跳过，artifacts/acceptance/fr01-fixed-commit-full/。纯.NET真实BGE分支解析与Git独立结果一致；真实config.json下载清单记录master与40位固定Commit，artifacts/acceptance/fr01-live-branch-snapshot/。编写测试时出现过一次原始字符串编译错误，修正后以上回归全绿。
- 2026-09-06 FR-01版本列表：全套155项通过、0跳过，artifacts/acceptance/fr01-revisions-full/。新增版本API/CLI对真实BGE返回master分支，明确不推断头Commit；命名引用固定化仍未完成。
- 2026-09-06检查点后真实进程故障通过：artifacts/rollout/revocation-crash-863da3cd65be4cee964f8a5205e8d083/crash-report.json。自有写进程退出-9，独立恢复进程退出0；撤销模型和旧检查点继续拒绝。演练程序编译0警告/0错误。仅证明完整保存后进程终止恢复；写中断、掉电、自动持久化和生产可信锚发布仍待验收。
- 2026-09-06真实BGE签名撤销通过：artifacts/rollout/p5-03-signed-bge/report.json 的signedRevocation验证撤销前金标准、已加载会话/新会话阻断、检查点恢复阻断及旧代拒绝。实验运行开关已开启，仍不能绕过。与历史发布对象撤销证据分开；真实进程故障和自动分发仍未验收。
- 2026-09-06 P5-03检查点：Hub18 + Runtime118 + ASP.NET9 + Cli5 =150项通过、0跳过，artifacts/acceptance/p5-03-checkpoint-final/。4项新增案例验证签名历史恢复、可信锚拒绝旧代/删改、过期阻断和原始签名复核；此处使用新策略对象恢复，不代表真实进程崩溃验收。自动持久化及独立可信锚发布仍是关闭条件。
- 2026-09-06 P5-03启动门禁：新策略无有效撤销清单即拒绝；未登记运行时亦不能绕过过期门禁。27项策略专项及全套146项通过、0跳过，artifacts/acceptance/p5-03-startup-full/；真实BGE签名初始清单加载后灰度/回滚/金标准复验通过，artifacts/rollout/p5-03-startup-bge/report.json。跨重启持久化和签名撤销自动分发仍待完成。
- 2026-09-06 Docker恢复后实测：旧镜像源码漂移，已用当前源码重建独立标签p5-current-local；12项本机ARM64隔离/配额/源码一致性检查通过，artifacts/container-checks/907c16c47d744c2aac6cf8d2f07c22d2/report.json。旧失败报告保留。新镜像扫描、实际模型推理、目标集群与长稳门槛不在本轮范围内，仍需验收。
- P5-03本轮：Release构建0警告/0错误；Hub18 + Runtime112 + ASP.NET9 + Cli5 =144项通过、0跳过，证据目录artifacts/acceptance/p5-03-final/。新增7项撤销案例覆盖缓存、新会话、执行中普通及流式响应、签名/范围/时间和进程内重放；跨重启防重放、首次清单门禁、自动分发和真实模型撤销演练仍未完成，详见docs/certification-revocation.md。该目录的final仅指本轮回归，不代表项目最终验收。
- P5-02真实在线报告：artifacts/upstream-checks/e4430c4a11724b0fa0b61496bb26c1d3/report.json。六仓库中五个无变化；Qwen仅新增LICENSE/README/FP16及修改.gitattributes。原固定GGUF通过新一轮复认证：artifacts/certification-runs/ab540475cb184c1aa10be32d19506a4f/summary.json。9项工具测试通过；远程CI仍未执行。首次Python缺少CA的失败报告单独保留，不计作成功。
- P5-01本机两轮五模型全绿：artifacts/certification-runs/70f2d4816ca14ce7abf87c0ba263337d/summary.json和1531fd782eb84293a0f50dcbf1fb83db/summary.json。第二轮从Release构建开始；全部报告独立生成并逐项检查。6项流水线负面测试通过。远程CI没有执行证据，状态未关闭。
- P2-04检查点：Hub18 + Runtime105 + ASP.NET9 + Cli5 =137项全绿；artifacts/acceptance/p2-04-final/和p2-04-cache-final/。新生产策略真实BGE演练：artifacts/rollout/p2-04-policy-bge/report.json。前次118项是历史检查点。
- 本轮最终构建：0 警告/0 错误；Hub18 + Runtime86 + ASP.NET9 + Cli5 = 118项全部通过，0跳过，记录在 artifacts/acceptance/2026-09-05/final/。该目录的“final”指本轮代码测试，不代表项目最终验收。
- P4-06全套117项测试：artifacts/acceptance/2026-09-05/p406-final/。
- 后续Hub离线修复：artifacts/acceptance/2026-09-05/offline-cache-fix/。
- 真实模型：artifacts/rollout/p4-06-bge/report.json。
- 执行手册：docs/runbooks/canary-rollback.md。
- 验收根目录保留了一次签名字段升级期间的失败运行，以明确命名的后续目录为准，不把失败证据混计为通过。
