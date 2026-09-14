# C# 原生能力扩展路线

更新：2026-09-09。

## 决策

在既有多运行时架构上继续扩展 C#，但只复写稳定协议、通用任务契约、标准 ONNX 前后处理和 .NET 服务治理能力。ModelScope Python 的动态模型类、Trainer、Exporter、复杂 Pipeline、自定义算子和训练数据处理继续由 Python Worker 承担，不按源码目录逐文件迁移。

本路线是迁移阶段 6。它不会把尚未完成的生产 1.0 外部门槛自动视为通过；私有仓库、许可证、目标平台、长稳、远程 CI、可信存储和负责人批准仍由原验收项跟踪。

## 固定实施顺序

| 顺序 | 工作项 | 首个可交付结果 | 完成门槛 |
|---:|---|---|---|
| 1 | ONNX Execution Provider 与设备策略 | CPU/CUDA/TensorRT/DirectML/CoreML 配置模型、可用性探测和默认拒绝策略 | CPU 回归通过；请求的 Provider 不可用时返回稳定错误；CPU fallback 必须显式；先冻结本次批准的目标 Provider/OS/硬件组合，再让每个批准组合完成真实初始化、固定模型推理、资源和金标准认证 |
| 2 | Dataset Hub 只读下载 | RepositoryId/RepositoryType、数据集详情、Revision、文件和快照下载 | 与模型共用安全路径、固定 Revision、Range、Hash、过滤、并发、缓存及离线语义；公开与获授权受限数据集契约通过 |
| 3 | 缓存删除、配额和回收 | list/remove/prune/dry-run | 只删除 Manifest 可证明无引用的内容；与下载锁协调；活动快照不可删；容量、LRU、损坏与中断恢复测试通过 |
| 4 | 通用类型化任务契约 | `ModelScope.Net.Tasks` 包 | 文本、图像、音频、视频输入及分类、Embedding、检测、Mask、关键点、文本/媒体输出具备有界校验；ONNX/GGUF/Python/Remote 可复用 |
| 5 | NER、文本排序和 CLIP ONNX 适配器 | 三类固定模型族适配器 | 每类至少一个固定 Revision，Python 金标准、普通/批量调用、资源边界、CLI/DI 和生产策略证据通过 |
| 6 | 可选 ASP.NET Core 推理网关 | 独立 `ModelScope.Net.Server` 包 | 模型发现、普通推理、SSE、OpenAPI、鉴权、租户限流、请求上限和遥测测试通过；基础 SDK 不自动暴露网络端点 |
| 7 | Hub 写入 API | 创建仓库、上传文件/目录、Commit 与 Tag | 先确认业务需求和权限模型；幂等、续传、冲突、摘要、取消、审计和负面权限测试通过，不通过 shell 拼装 Git 命令 |

不得跳过前一项的工程门槛去声明后一项完成。研究、平台准备和不修改同一公开契约的测试资产可以并行；实现状态只能按上述顺序关闭。本路线列出五种可选 Provider，不表示发布必须无条件认证全部五种；P6-01 的退出集合以 P0-06/发布清单书面批准的目标组合为准。目标集合尚未冻结时，P6-01 不能关闭。

## P6-01 设计基线

ONNX 设备选择由 `OnnxRuntimeOptions` 明确配置，不从机器环境或模型名称猜测。默认仍为 CPU，以保持现有调用兼容。

最小配置维度：

- `ExecutionProvider`：CPU、CUDA、TensorRT、DirectML 或 CoreML；
- `DeviceId`：非负设备编号；
- `AllowCpuFallback`：默认 `false`，只有显式开启才允许请求的加速 Provider 不可用时退回 CPU；
- `GpuMemoryLimitBytes`：可选、正数，仅传给 CUDA 分配器；不是调度准入、进程总显存上限或操作系统强制显存隔离；
- Provider 实际可用性来自当前 ONNX Runtime 原生库，不根据 NuGet 包名或操作系统推断。

失败语义：

- 配置值非法：`InvalidRequest` 或启动配置异常；
- 请求的 Provider 未包含在当前原生 ONNX Runtime：`RuntimeNotInstalled`；
- Provider 已列出但配置或初始化失败：`RuntimeNotInstalled`，不自动 fallback；
- Provider 配置成功后 `InferenceSession` 创建失败：`ModelLoadFailed`；当前不能可靠地从原生异常正文区分驱动、算子或模型格式根因；
- 推理期间 Provider 失败：`InferenceFailed`；
- 显式 CPU fallback 生效时，能力诊断和响应警告必须可见，不能把结果记录为原请求 GPU Provider 的认证证据。

代码强制精确匹配 requested/effective Provider、requested/effective Device ID、ONNX Runtime 原生版本、allow/used fallback，并由 Catalog 另行绑定平台、运行时摘要、模型 Revision 和制品摘要。驱动/CUDA/cuDNN/TensorRT 版本当前只属于待补的目标环境证据，尚未由运行时代码自动采集。仅完成配置接线或看到 Provider 名不等于 GPU 认证。

## 数据集边界

阶段 6 只迁移 Dataset Hub 仓库访问，不迁移 `MsDataset` 的 DataFrame/Arrow 封装、训练 DataLoader、数据增强或任意 Python transform。模型与数据集统一使用 `RepositoryType` 区分，缓存身份必须包含仓库类型，禁止同名模型和数据集互相命中。

## 原生任务选择规则

只有同时满足以下条件的模型族进入 C# ONNX 适配器：

1. 图和输入输出张量契约可固定；
2. Tokenizer/Processor 可以安全解析且无仓库代码执行；
3. 前后处理能以有界内存实现；
4. 至少有一个固定 Revision 的 Python 金标准；
5. 与 Python 相比能减少部署依赖、跨进程复制或稳定延迟。

Stable Diffusion、复杂 OCR/FunASR、视频生成、3D、多阶段多模态和自定义 CUDA 算子不因已有 Python Pipeline 而自动进入 C# 复写范围。

## 状态与证据

唯一任务状态仍记录在 [`migration-progress.md`](./migration-progress.md)。技术方案描述稳定边界，迁移计划描述顺序和门槛，进度表只在实现与对应自动化证据同时存在时更新为 `COMPLETED`。

截至 2026-09-09，P6-01 的公共配置、CLI/DI 接线、原生 Provider 探测与固定模型 Session probe、默认拒绝、显式 CPU fallback、稳定错误、普通/流式响应警告、遥测维度和生产 Catalog 精确属性匹配已经实现。Release 构建 0 warning/0 error，Hub 43 + Runtime 219 + ASP.NET Core 12 + CLI 8 = 282 项通过，0 失败/0 跳过。本机 ORT 1.29.0 实际枚举 CoreML、CPU 和当前公开枚举不支持的 WebGPU；CoreML 已在无 CPU fallback 下完成合成 Add 图 Session/推理 smoke，但仍无固定业务模型、资源或金标准证据。当前工程仍只直接依赖 CPU 版 ONNX Runtime，批准的目标 Provider 集合尚未冻结，CUDA/TensorRT/DirectML 的目标原生包/驱动证据及所有批准组合的完整认证均未形成。因此 P6-01 保持 `IN_PROGRESS`，不得提前开始 P6-02。
