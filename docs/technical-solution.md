# ModelScope.Net 技术方案

## 1. 方案摘要

ModelScope.Net 在 .NET 平台提供统一的模型仓库访问和模型调用能力。它不把 ModelScope Python 生态完整重写为 C#，而是采用“原生下载、能力检测、多运行时路由、安全隔离”的架构：

- 符合已支持 Hub 协议、且用户有权限访问的仓库通过原生 .NET Hub 客户端下载。
- 支持 API Inference 的模型通过远程 HTTP API 调用。
- 认证的 ONNX 模型通过 ONNX Runtime 本地调用。
- 认证的 GGUF 模型通过 llama.cpp 兼容运行时调用。
- 经 Catalog 认证或检测为兼容、且安全策略允许的 PyTorch、Transformers、Diffusers、ModelScope Pipeline 和自定义代码模型通过对应等级的 Python Worker 调用。
- 无法安全识别或缺少依赖的模型返回结构化诊断。

1.0 基线为 .NET 10 LTS。首期支持 Windows 11/Windows Server 2022 或 2025 x64 CPU，以及 Ubuntu 24.04 x64 CPU。Linux NVIDIA GPU 作为生产目标，但 CUDA、cuDNN 和 ONNX Runtime 组合必须在阶段 0 的发布清单中冻结。Windows GPU、ARM64 和 macOS 不属于默认 1.0 承诺，需单独认证。

## 2. 背景与约束

ModelScope 模型广场同时托管 ONNX、GGUF、Safetensors、PyTorch、TensorFlow、Transformers、Diffusers、LoRA、Llamafile、MLX、OpenVINO 以及任意社区代码。模型仓库是一组版本化文件，不是统一的可执行模型 ABI。

现有 ModelScope Python 项目约有 57 万行 Python，其中模型实现约 42 万行；大量文件直接依赖 PyTorch，并包含 TensorFlow、OpenCV、MMCV、自定义 C/CUDA 算子、动态插件和远程代码机制。完整纯 .NET 重写会失去 Python AI 生态兼容性，并形成长期追赶上游的维护负担。

因此，本方案明确区分：

- 下载成功：模型文件已按固定 Revision 完整、可信地落入缓存。
- 可检测：能够识别任务、格式、架构或依赖。
- 可加载：某个运行时能够创建模型会话。
- 可调用：输入输出契约已实现。
- 已认证：固定模型 Revision、运行时和适配器已通过金标准及性能测试。

## 3. 目标与非目标

### 3.1 目标

- 通过 Model ID 和 Revision 查询、下载并缓存符合已支持 Hub 协议、且用户有权限访问的模型。
- 返回模型大小、许可证、格式、架构、依赖和运行时能力。
- 为常见任务提供统一的异步和流式 .NET 接口。
- 自动选择已认证运行时，并允许显式覆盖。
- 支持 Remote API、ONNX、GGUF 和 Python Worker。
- 对不支持模型返回原因、缺失依赖和可执行建议。
- 建立可版本化的兼容性清单和持续认证机制。

### 3.2 非目标

- 不重写 ModelScope、Transformers、Diffusers 或 PyTorch 全部实现。
- 不承诺纯 .NET 本地执行任意社区模型。
- 1.0 不提供训练、微调或分布式训练。
- 不自动执行未经允许的模型仓库代码。
- 不绕过受限访问、许可证或第三方使用条款。
- 不把免费 API Inference 当作有商业 SLA 的唯一后端。

## 4. 功能与质量要求

| 编号 | 要求 |
|---|---|
| FR-01 | 查询模型信息、版本和文件列表 |
| FR-02 | 支持快照、单文件和模式过滤下载 |
| FR-03 | 支持 Token、私有及受限仓库 |
| FR-04 | 支持并发、续传、校验、重试和取消 |
| FR-05 | 支持内容缓存、Revision 快照和离线模式 |
| FR-06 | 检测格式、架构、任务、依赖、许可证和资源需求 |
| FR-07 | 返回可用运行时及不兼容原因 |
| FR-08 | 统一执行普通、异步和流式推理 |
| FR-09 | 固定 Model ID、Revision、运行时和适配器版本 |
| FR-10 | 提供日志、指标、追踪、健康检查和审计 |

质量要求：

- 下载失败不能破坏已验证缓存，并应支持断点恢复。
- Token 不得进入普通日志、URL、异常或遥测。
- 未显式允许时，仓库代码不得在宿主进程执行。
- 模型调用必须支持取消、超时和并发限制。
- SDK 公开 API 使用语义化版本；运行时插件可独立升级。
- 模型、运行时和适配器固定时，制品与路由选择应可重复。
- 推理结果只在固定硬件、固定随机种子和声明容差内要求可重复；不承诺跨硬件位级一致。

## 5. 总体架构

~~~text
.NET Application / ASP.NET Core / CLI
                    |
             ModelScope.Net SDK
                    |
   +----------------+----------------+
   |                |                |
HubClient       ModelInspector   PolicyEngine
   |                |                |
CacheStore      RuntimeRouter---------+
                    |
     +--------------+---------------+
     |              |               |
Remote API      ONNX / GGUF      Python Worker
~~~

建议拆分为：

| 包 | 职责 |
|---|---|
| ModelScope.Net.Abstractions | DTO、任务契约、异常和运行时接口 |
| ModelScope.Net.Hub | 查询、鉴权、下载、缓存和快照 |
| ModelScope.Net.Runtime | 能力检测、策略和运行时路由 |
| ModelScope.Net.Runtime.Remote | ModelScope API Inference |
| ModelScope.Net.Runtime.Onnx | ONNX Runtime 与任务适配器 |
| ModelScope.Net.Runtime.Gguf | llama.cpp/GGUF 适配 |
| ModelScope.Net.Runtime.Python | Python Worker 客户端和生命周期 |
| ModelScope.Net.AspNetCore | 依赖注入、健康检查、指标和托管服务 |
| ModelScope.Net.Cli | 登录、查询、下载、检查、调用和缓存命令 |

## 6. Hub 与缓存设计

HubClient 与推理解耦，负责 Model ID、Endpoint、Revision、Token 和文件清单。

下载流程：

1. 规范化 Model ID、Revision 和 Endpoint。
2. 查询模型元数据，将分支或 Tag 解析为确定性 Revision。
3. 获取文件清单并应用允许/忽略规则。
4. 检查许可证、访问权限、本地容量和下载配额。
5. 以临时文件并发下载，支持 HTTP Range。
6. 校验大小、ETag 或内容 Hash。
7. 原子提交 Blob 并创建快照 Manifest。
8. 更新缓存索引和最后访问时间。

推荐缓存：

~~~text
cache/
  blobs/<content-hash>
  snapshots/<owner>/<model>/<revision>/
  manifests/<owner>/<model>/<revision>.json
  locks/
  temp/
~~~

关键规则：

- Blob 内容寻址，多个 Revision 可共享同一文件。
- Manifest 记录 Model ID、Commit、Endpoint、文件 Hash、许可证和创建时间。
- 下载到临时区，校验成功后原子提交。
- 多进程使用文件锁，锁具有超时和崩溃恢复。
- 支持导入现有 ModelScope 缓存，但不依赖 Python SDK 私有目录结构。
- 生产默认固定 Commit，不自动追踪可变分支。

## 7. 模型能力检测

ModelInspector 不执行仓库代码，只读取文件名和可安全解析的配置：

- configuration.json、config.json、model_index.json。
- generation、Tokenizer 和 Processor 配置。
- ONNX 图元数据。
- GGUF Header。
- 模型卡 YAML 元数据。

能力状态：

| 状态 | 含义 |
|---|---|
| Detected | 发现候选格式或配置 |
| Compatible | 静态检查未发现已知阻断，但尚未证明能够加载 |
| LoadVerified | 固定环境中已成功创建会话，但尚未完成结果认证 |
| Certified | 固定组合已通过金标准和性能测试 |
| Unavailable | 当前机器缺少运行时或资源 |
| Unsupported | 架构、算子、任务或策略不支持 |

能力报告至少包含：

- Model ID 和实际 Revision。
- 任务与架构候选。
- 模型文件、量化、精度和估计资源。
- 许可证及访问状态。
- 候选运行时、认证状态和不兼容原因。
- 是否包含自定义代码、依赖文件或远程执行要求。

## 8. 运行时路由

开发环境默认优先级：

1. 用户显式指定且策略允许的运行时。
2. 已认证的本地 ONNX。
3. 已认证的本地 GGUF。
4. 可用且满足策略的 ModelScope API Inference。
5. 允许的隔离 Python Worker。
6. 返回不支持诊断。

生产环境只自动选择 Certified 条目。显式选择 Experimental 或未认证运行时必须启用单独策略，并在响应和审计日志中标记；默认禁止。生产降级只能发生在发布清单允许的运行时之间。

确定性编排流程：

1. 如果用户显式选择 Remote API，先查询远端能力，不要求下载完整快照。
2. 其他情况先查询元数据；只有本地运行时或 Python Worker 需要制品时才下载。
3. ModelInspector 生成候选，但不把静态检测结果视为加载成功。
4. Router 将候选与 Catalog、机器资源、许可证和安全策略求交集。
5. 选择第一个允许的运行时并尝试加载。
6. 加载失败时，只有 Catalog 明确配置自动回退且不降低隐私/安全等级，才尝试下一运行时。
7. 从本地切换到 Remote、从安全代码切换到远程代码，必须由用户显式确认。
8. 所有回退都写入响应元数据、指标和审计日志。

路由同时考虑：

- 任务和模型架构。
- 输入输出及流式能力。
- OS、CPU 指令集、GPU、显存和内存。
- ONNX Opset、自定义算子和动态 Shape。
- 数据隐私、网络策略和商业 SLA。
- 模型许可证和远程代码安全等级。

不得只根据文件扩展名选择运行时。Safetensors 只是张量容器；ONNX 仍可能缺少前后处理或自定义算子；GGUF 也需要目标运行时支持具体架构。

## 9. Remote API Runtime

Remote 后端负责：

- 使用 ModelScope Token 调用 API Inference。
- 支持 OpenAI 兼容的文本/多模态接口和平台专用端点。
- 支持流式响应、取消、超时和幂等重试。
- 解析限流响应头，暴露剩余额度和重试时间。
- 区分不支持、下线、限流、鉴权失败和服务故障。

免费 API 只适合开发、体验或用户明确接受限制的场景。生产部署必须能够配置商业服务或本地后端，并具有熔断和降级。

## 10. ONNX Runtime

ONNX 后端由执行器和任务适配器组成。

执行器负责：

- Session 生命周期、并发和线程安全。
- CPU、CUDA 等 Execution Provider。
- Tensor Buffer 复用和资源释放。
- 动态 Shape、批处理和模型热加载。
- 内存、显存和并发上限。

任务适配器负责：

- Tokenizer、图像、音频预处理。
- 模型输入输出映射。
- 文本生成循环、采样、停止条件和 KV Cache。
- 分类、检测、分割等后处理。
- 多模型 Pipeline 调度。

首期认证顺序：

1. 文本 Embedding。
2. 文本分类。
3. 图像分类。
4. 标准目标检测。
5. 标准 Encoder 模型。

生成式、多模态、音频和扩散模型在基础能力稳定后单独排期。

## 11. GGUF Runtime

首期通过独立 llama-server 或等价受控进程接入，降低原生生命周期、崩溃和内存管理对 ASP.NET Core 宿主的影响。

支持范围使用架构白名单。Llama、Qwen、Mistral 等属于逐个认证的候选架构，不因名称或 `.gguf` 扩展名自动获得支持；当前技术预览的默认白名单仅包含已经实测的 `qwen2`/Q2_K/`gpt2` 组合。

- 当前实现文本生成与 Chat Completion；Embedding 作为后续候选适配器。
- 已验证量化类型、Tokenizer 和 Chat Template。

GGUF 是容器格式，不是通用运行接口。ComfyUI 专用、视频生成、图像生成或自定义 GGUF 不自动进入支持范围。

P3-06 已实现 llama-server 管理/外部服务模式、OpenAI/SSE 调用、回环绑定、临时随机 API Key、崩溃后拉起及宿主退出清理，并完成 Qwen2.5 0.5B Q2_K 在固定 llama.cpp/macOS ARM64/CPU 上的真实认证。其他平台、架构、GPU 与性能结论仍需分别认证，详见 [GGUF llama.cpp 运行时](./gguf-runtime.md)。

## 12. Python Runtime

Python 执行载体由安全等级强制决定：只有 Safe 可使用普通独立进程；TrustedModel 必须使用已签名专用容器；IsolatedUntrusted 必须使用一次性沙箱容器或微虚机。默认不通过 Python.NET 嵌入 ASP.NET Core。

协议建议：

- 同机：gRPC 配合 Unix Domain Socket 或 Named Pipe。
- 跨主机/Kubernetes：gRPC/TLS。
- 大型图片、音频和视频：共享文件、对象存储或共享内存，避免 Base64。

Worker 负责：

- 复用 ModelScope pipeline、Transformers 和 Diffusers。
- 管理 Python 环境、GPU、模型加载和卸载。
- 返回依赖、资源和兼容性诊断。
- 支持健康检查、超时、重启和资源回收。

安全等级：

| 等级 | 行为 |
|---|---|
| Safe | 只使用已安装和已审核代码 |
| TrustedModel | 允许白名单发布者和固定 Revision 代码 |
| IsolatedUntrusted | 最小凭据、最小网络的隔离容器 |
| Denied | 策略禁止执行 |

远程代码必须显式授权并固定 Commit。公共仓库不等于可信仓库。

## 13. 统一调用接口

底层模型差异很大，核心 SDK 同时提供通用 Envelope 和强类型任务接口。

通用会话包含：

- 普通异步调用。
- 流式调用。
- 能力报告。
- 取消和异步释放。

强类型接口按整体路线规划如下：

- ITextGenerationModel
- IEmbeddingModel
- ITextClassificationModel
- IImageClassificationModel
- IObjectDetectionModel
- ISpeechRecognitionModel

1.0 必须实现 Embedding、文本分类和文本生成；其中文本生成可以由 Remote、GGUF 或 Python 提供。图像分类和目标检测至少选择一项进入 1.0。语音识别接口可以发布为预览，但不属于默认 1.0 验收范围。

每次响应必须返回 Model ID、实际 Revision、运行时及版本、Schema 版本、耗时、警告和降级信息。

## 14. 兼容性 Catalog

兼容承诺的最小单位是：

模型家族或 Model ID + Revision + Runtime + Adapter。

Catalog 支持模型级、架构级和任务级规则，具体模型规则优先。条目包含：

- Model ID/Revision。
- 任务和架构。
- 运行时、制品和适配器版本。
- 认证状态。
- 数值容差和性能基线。
- 远程代码、许可证和安全策略。
- 对应自动化测试 ID。

运行时、适配器或上游 Revision 变化后必须重新认证。生产默认只使用 Certified 条目。

## 15. 安全与合规

- Token 使用系统凭据存储或 Secret Manager。
- 日志和遥测清除 Token、Cookie、签名参数和敏感输入。
- 模型许可证与代码许可证分别记录；可下载不代表可商用。
- 受限模型保留授权和许可接受记录。
- 下载文件进入缓存前检查路径穿越、软链接和压缩炸弹。
- Python Worker 使用非 Root、只读根文件系统、资源限额和最小网络权限。
- 远程代码固定 Commit，记录代码 Hash 和审计事件。
- 模型输出审核以扩展点提供，不硬编码具体业务政策。

执行隔离基线：

| 等级 | 执行载体 | 网络 | 凭据与挂载 | 进程与资源 |
|---|---|---|---|---|
| Safe | 独立 Worker 进程或已签名容器 | 默认仅访问 Hub | 只读模型快照，无宿主凭据 | CPU/GPU、内存、时间和并发限额 |
| TrustedModel | 已签名专用容器 | 默认拒绝，按域名放行 | 临时只读 Token，工作目录可写 | 非 Root、只读根文件系统、禁止特权 |
| IsolatedUntrusted | 一次性沙箱容器/微虚机 | 完全禁网，除非一次性审批 | 无宿主凭据，只读模型挂载，临时目录配额 | 系统调用过滤、PID/内存/磁盘/GPU 限额 |
| Denied | 不执行 | 无 | 无 | 无 |

Windows 首期不执行 IsolatedUntrusted；这类模型必须转交 Linux 隔离节点或拒绝。TrustedModel 仍必须运行在容器中，不能进入 ASP.NET Core 宿主。

## 16. 可观测性

关键指标：

- 下载吞吐、失败率、重试、续传和缓存命中率。
- 模型加载、冷启动、内存和显存。
- 推理吞吐、P50/P95/P99 延迟、排队和错误率。
- 路由分布、降级原因和不支持原因。
- API Inference 限流和剩余额度。
- Worker 重启、OOM 和健康状态。

结构化日志至少包含 Trace ID、Model ID、Revision、Runtime、Task 和错误码；默认不记录提示词、图片、音频或模型输出。

## 17. 部署模式

### 嵌入式 SDK

适合桌面、离线和单机服务。ONNX 进程内运行，GGUF 可使用受控子进程；Python 必须遵守第 15 节安全等级，只有 Safe 能使用普通子进程，其他等级必须转交相应容器/微虚机或拒绝。

### ASP.NET Core 网关

适合团队共享和多模型服务。网关负责认证、配额、路由和监控，模型 Worker 独立伸缩。

### Kubernetes

按 CPU ONNX、GPU ONNX、GGUF/LLM、Python Worker 和远程代码隔离 Worker 划分节点池。使用节点缓存、预热卷或对象存储避免 Pod 重复下载。

## 18. 错误模型

至少区分：

- ModelNotFound
- RevisionNotFound
- AuthenticationRequired
- LicenseAcceptanceRequired
- DownloadIntegrityFailed
- InsufficientDiskSpace
- RuntimeNotInstalled
- ArchitectureUnsupported
- OperatorUnsupported
- RemoteCodeNotAllowed
- InsufficientMemory
- RemoteApiUnavailable
- RemoteApiRateLimited
- ModelLoadFailed
- InferenceFailed

错误对象包含是否可重试、建议运行时、缺失依赖和可执行修复建议。

## 19. 测试与验收

测试层级：

- Hub 单元、集成和在线契约测试。
- ONNX、GGUF、Safetensors 和配置解析测试。
- 任务适配器输入输出测试。
- 与官方 Python 实现的金标准比较。
- 冷启动、吞吐、延迟、内存和显存测试。
- 路径、Token、未知代码和 Worker 隔离安全测试。
- 网络中断、磁盘满、Worker 崩溃和 GPU OOM 故障测试。

1.0 验收：

- 对发布清单中的 Hub 协议契约套件，连续 7 天、至少 2,000 次下载尝试成功率不低于 99.5%；鉴权拒绝、用户取消和平台明确限流不计入服务失败。
- 下载可恢复，且不会重复传输已验证内容。
- Token 不出现在日志、异常和遥测。
- Remote、ONNX、Python 三类运行时端到端可用。
- GGUF 至少认证一个发布清单指定的文本生成 Model ID/Revision。
- 每个认证条目必须包含 Model ID、Commit、任务、硬件、运行时、适配器、金标准数据、容差和性能门槛。
- release-manifest-v1.yaml 必须为 Embedding、文本分类、文本生成，以及图像分类/目标检测二选一的每个必选任务指定至少一个 Certified Model ID/Revision、运行时和适配器；缺少任一必选任务即不能签版。
- 不支持模型返回明确且可操作的原因。
- Python Worker 崩溃不导致宿主进程退出。
- 固定模型、运行时和适配器后制品及路由可重复；数值结果按条目定义的硬件、Seed 和容差验收。

阶段 0 必须生成 release-manifest-v1.yaml，冻结测试模型、平台矩阵、成功率分母、数值容差、性能门槛和例外。该文件未批准时不得进入 1.0 签版。

P4-02 已在 `RuntimeRouter` 会话边界加入可选仪表器。ASP.NET Core 默认注册 `ModelScope.Net.Runtime` ActivitySource/Meter 与结构化审计，仅记录受控任务/运行时、结果、耗时、稳定错误码、Trace ID 和不可逆模型指纹；请求正文、Token、输出、异常消息、私有模型 ID 与本地路径明确排除。具体字段、指标和接入方式见 [OpenTelemetry 与审计](./telemetry-audit.md)。

P4-03 已完成本机托管 Worker 的应用层隔离：子进程环境白名单、敏感变量拒绝、诊断默认关闭、回环强制、Python gRPC 临时 Bearer Key、模型根目录白名单、请求/会话上限和完整进程树回收。P4-05 随后关闭只读挂载、外网默认拒绝、非 root/capability/seccomp、OS 级资源配额及跨主机 mTLS 五项部署控制，详见 [Worker 安全隔离](./worker-security.md) 与机器可读矩阵。

P4-04 已在 `RuntimeRouter` 之上加入宿主级 `ModelSessionPool`：按固定模型身份复用 Session，支持显式预热、非活跃项 LRU、全局与单模型租约门禁、有限等待队列、估算内存准入和空闲回收。活动租约及待授予请求均不可驱逐，容量拒绝使用可重试的稳定错误码；健康检查和快照只公开模型指纹及容量计数。该机制是应用层背压而不是 OS 强制配额，边界与接入方式见 [模型资源治理](./model-resource-governance.md)。

P4-05 已完成 Linux Worker 的容器部署基线：只读模型和根文件系统、独立限额临时目录、默认拒绝出站、网关白名单入口、非 root/capability/seccomp、CPU/内存/临时存储/PID/GPU 配额，以及 Python gRPC Worker 与 .NET 客户端的双向 TLS。Windows CPU 首期仅允许认证 ONNX/Remote，本地 Python 和未知代码保持拒绝。清单控制、外部前置条件与运行验证边界见 [容器与部署基线](./deployment-p4-05.md)。

## 20. 关键风险与决策

| 风险 | 缓解 |
|---|---|
| ModelScope 接口变化 | Endpoint 抽象、契约测试和兼容层 |
| 元数据不完整导致误判 | 候选/认证分级、兼容性 Catalog |
| ONNX 算子或前后处理不兼容 | 金标准测试和 Python 降级 |
| GGUF 架构不支持 | 架构白名单和 Header 检查 |
| 远程代码供应链风险 | 默认拒绝、固定 Commit、容器隔离 |
| 大模型消耗磁盘和网络 | 文件过滤、内容缓存、预热和配额 |
| Python 依赖冲突 | 依赖锁定和按运行时镜像隔离 |
| 上游快速更新 | 固定 Revision 和持续认证 |
| 免费 API 限流或下线 | 本地/商业后端、熔断和降级 |

关键决策：

1. 下载能力使用原生 .NET 实现。
2. 推理使用多运行时，不建设单一万能后端。
3. 下载成功与模型可调用是两个独立状态。
4. 默认不执行远程模型代码。
5. Python 使用独立 Worker，不嵌入核心宿主进程。
6. 生产必须固定模型 Revision。
7. 产品承诺限定为发布清单定义的 Hub 协议、平台、运行时和认证模型；样本验证不自动扩展为全量承诺。

## 21. 参考

- https://www.modelscope.cn/models
- https://www.modelscope.cn/docs/models/download
- https://modelscope.cn/docs/model-service/API-Inference/limits
- https://onnxruntime.ai/docs/get-started/with-csharp.html
- https://onnxruntime.ai/docs/execution-providers/
- https://github.com/dotnet/TorchSharp
- https://pythonnet.github.io/pythonnet/dotnet.html
- https://dotnet.microsoft.com/en-us/platform/support/policy
