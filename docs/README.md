# ModelScope.Net 文档

## 项目定位

ModelScope.Net 的目标不是把 ModelScope Python 生态完整重写为 C#，而是在 .NET 平台提供统一的模型发现、下载、能力识别和调用接口。

系统采用多运行时架构：

- 符合已支持 Hub 协议、且用户有权限访问的 ModelScope 模型仓库可通过统一 Hub 客户端下载。
- 支持 ModelScope API Inference 的模型通过远程 HTTP API 调用。
- 经过认证的 ONNX 模型通过 ONNX Runtime 在 .NET 进程内运行。
- 经过认证的 GGUF 模型通过 llama.cpp 兼容运行时调用。
- PyTorch、Transformers、Diffusers、ModelScope Pipeline 和自定义代码模型通过隔离的 Python Worker 运行。
- 无法安全识别或缺少运行依赖的模型返回结构化诊断，不自动执行未知代码。

## 文档目录

- [上游与依赖变化检测](./upstream-monitoring.md)：固定Commit/分支清单比较、依赖漂移、查询失败与CI处理。
- [持续认证流水线](./continuous-certification.md)：五模型登记、重复执行、失败留痕与CI接入。
- [生产路由认证策略](./production-runtime-policy.md)：精确签名授权、制品校验和远程回退接入。
- [威胁模型](./security/threat-model.md)及[许可证证据流程](./security/license-review-workflow.md)：工程审查材料和未完成批准。
- [P4-06 灰度与回滚](./runbooks/canary-rollback.md)：签名发布、四级放量、回滚与真实模型演练。
- [验收审计台账](./acceptance-audit.md)：按原项目与 FR 要求记录证据、缺口和最终门槛。
- [2026-09-08 最终验收复核](./final-acceptance-2026-09-08.md)：原源码/方案逐项差异、本轮260项回归和生产1.0 NO-GO关闭条件。
- [技术方案](./technical-solution.md)：目标架构、组件设计、运行时路由、安全、接口和验收标准。
- [迁移计划](./migration-plan.md)：阶段划分、里程碑、人员配置、测试策略、风险控制和交付标准。
- [迁移进度表](./migration-progress.md)：中断恢复、任务状态、验收证据和发布检查点。
- [P3-07 发布模型认证门禁](./release-certification.md)：5 个固定 Revision 本地模型、任务覆盖、报告回链与审批边界。
- [P3-08 CPU 性能与容量基线](./performance-capacity.md)：5 个模型的加载、p95、吞吐、工作集、预览门槛与容量建议。
- [P4-02 OpenTelemetry 与审计](./telemetry-audit.md)：推理追踪、指标、安全审计字段、敏感信息排除策略与接入方式。
- [P4 运行时 Dashboard 与告警基线](./observability-runbook.md)：Prometheus 指标契约、Grafana 面板、告警规则、上线核对与未覆盖信号。
- [NuGet 技术预览分发](./distribution.md)：8 个对齐包、符号包、包内容验证和本地消费者冒烟。
- [升级与回退手册](./upgrading.md)：包、运行时、模型、Catalog 和撤销状态的统一升级单位与灰度回退步骤。
- [P4-03 Worker 安全隔离](./worker-security.md)：子进程环境、回环鉴权、模型根目录、容量上限和待完成的容器沙箱门禁。
- [P4-04 模型资源治理](./model-resource-governance.md)：Session 预热与复用、LRU、并发门禁、有限队列、估算内存配额和健康检查。
- [P4-05 容器与部署基线](./deployment-p4-05.md)：Linux CPU/GPU 沙箱、Windows CPU 拒绝策略、只读挂载、网络/资源配额及跨主机 mTLS。
- [下载缓存格式](./cache-format.md)：Blob、Snapshot、Manifest、锁和离线校验规则。
- [凭据存储](./credentials.md)：Token 优先级、文件权限和生产 Secret 建议。
- [Python Worker 协议](./python-worker-protocol.md)：健康、加载、调用、流式与恢复接口。
- [ONNX Runtime 原始张量协议](./onnx-runtime.md)：CPU 会话、JSON 张量格式、限制与错误边界。
- [ONNX 文本生成适配器](./onnx-text-generation.md)：byte-level BPE、KV cache、贪心生成、流式事件与固定模型认证边界。
- [ONNX Embedding 适配器](./onnx-embedding.md)：WordPiece、批处理、池化、归一化与认证边界。
- [ONNX 文本分类适配器](./onnx-text-classification.md)：标签映射、Softmax/Sigmoid、Top-K 与分类认证边界。
- [ONNX 图像分类适配器](./onnx-image-classification.md)：图像解码、缩放裁剪、归一化、Top-K 与输入资源边界。
- [GGUF Header 与预览白名单](./gguf-header-policy.md)：有界元数据解析、架构/量化/Tokenizer 放行条件与认证边界。
- [GGUF llama.cpp 运行时](./gguf-runtime.md)：受控进程、OpenAI/SSE 契约、安全边界、崩溃恢复与固定分发。
- [BGE 真实模型认证](../tests/compatibility/bge-small-en-v1.5/README.md)：固定 Revision、Python 金标准、.NET 输出与复验命令。
- [all-MiniLM-L6-v2 认证](../tests/compatibility/all-minilm-l6-v2/README.md)：固定 Revision、Mean Pooling 与 Python/.NET 数值一致性证据。
- [DistilBERT SST-2 认证](../tests/compatibility/distilbert-sst2/README.md)：固定 Revision、分类数值误差与标签一致性证据。
- [MobileNet V2 图像分类认证](../tests/compatibility/mobilenet-v2-image-classification/README.md)：固定 ONNX/源模型 Revision、预处理和类别一致性证据。
- [Qwen2.5 0.5B GGUF 认证](../tests/compatibility/qwen2.5-0.5b-instruct-gguf/README.md)：固定 Revision、Header 白名单与 macOS ARM64/CPU 真实 llama.cpp 推理证据。

## 核心结论

| 目标 | 可行性 | 结论 |
|---|---:|---|
| 下载公开及授权模型 | 很高 | 建设原生 .NET Hub 客户端 |
| 调用 API Inference 模型 | 很高 | 使用标准 HTTP/OpenAI 兼容协议 |
| 本地调用 ONNX 模型 | 高 | 按模型家族认证，不按文件扩展名承诺 |
| 本地调用 GGUF 模型 | 中高 | 首期限定主流 llama.cpp 兼容架构 |
| 调用 Python AI 生态模型 | 高 | 通过隔离 Python Worker 兜底 |
| 纯 .NET 本地调用任意社区模型 | 很低 | 不作为项目目标 |

## 推荐交付节奏

- 8～12 周完成可运行的技术预览版。
- 6～8 个月完成推荐范围的首个生产版本；缩减运行时和模型范围时可压缩至 4～5 个月。
- 后续按模型家族持续扩展认证范围。

## 参考

- [ModelScope 模型广场](https://www.modelscope.cn/models)
- [ModelScope 模型下载文档](https://www.modelscope.cn/docs/models/download)
- [ModelScope API Inference 限制](https://modelscope.cn/docs/model-service/API-Inference/limits)
- [ONNX Runtime C#](https://onnxruntime.ai/docs/get-started/with-csharp.html)
- [Python.NET 嵌入说明](https://pythonnet.github.io/pythonnet/dotnet.html)
