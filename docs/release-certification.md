# P3-07 发布模型认证门禁

P3-07 将单模型认证报告汇总为发布清单级自动门禁。门禁读取 `release-manifest-v1.yaml`，要求本地推理认证模型数量为 5～10、Revision 为完整 40 位 commit、认证范围明确、必选任务齐全，并逐项打开清单回链的 JSON 报告核对 Model ID、Revision 与 `passed` 状态。

## 当前通过模型

| 模型 | 任务 | 本地运行时 | 关键认证 |
|---|---|---|---|
| `BAAI/bge-small-en-v1.5` | Sentence Embedding | ONNX CPU | CLS Pooling，Python/.NET 8×384 数值对照 |
| `unsloth/all-MiniLM-L6-v2` | Sentence Embedding | ONNX CPU | Mean Pooling，Python/.NET 8×384 数值对照 |
| `distilbert/distilbert-base-uncased-finetuned-sst-2-english` | 文本分类 | ONNX CPU | 8 样本标签一致性和 logits/概率误差 |
| `onnx-community/mobilenet_v2_1.0_224-ONNX` | 图像分类 | ONNX CPU | 3 样本 Top-1/Top-5 和数值误差 |
| `Qwen/Qwen2.5-0.5B-Instruct-GGUF` | 文本生成 | llama.cpp CPU | 实际加载、非流式、SSE、崩溃恢复与清理 |

当前门禁覆盖 Embedding、文本分类、图像分类和文本生成。Hub-only 下载冒烟模型不计入 5 个本地推理认证名额。

## 自动验收规则

`ReleaseManifestCertificationTests` 执行以下检查：

1. `certified-preview*` 模型数量必须在 5～10 之间。
2. 每个模型必须具有固定 Revision、任务和认证 scope。
3. 必须覆盖 Sentence Embedding、文本分类、文本生成，以及图像分类/目标检测至少一项。
4. 每个模型必须通过 `certificationReport` 回链到可读取的报告。
5. 报告的 Model ID 和 Revision 必须与发布清单完全一致，状态必须为 `passed`。

单模型报告仍负责数值阈值、产物 SHA-256、平台和运行时专属断言；统一门禁不重复解释模型输出。

## 与发布审批的边界

P3-07 的 `COMPLETED` 表示最低 5 个固定 Revision 本地模型达到工程认证门槛，不表示 1.0 可以发布。以下事项仍独立阻止签版：

- `release-manifest-v1.yaml` 仍为 `draft`，产品、技术、安全和法务批准均未完成。
- MobileNet 源模型许可证仍需法务复核。
- Ubuntu、Windows、GPU 和 CUDA 平台矩阵尚未冻结或实机认证。
- P3-08 已完成 macOS ARM64/CPU 技术预览性能门禁；Windows、Linux、GPU/CUDA/Metal，以及阶段 4 安全运维和 72 小时稳定性测试尚未完成。
- 阶段 0 仍需由业务负责人把代表模型扩充到 15～25 个。

因此，P3-07 只解除“首批模型数量与任务覆盖不足”的工程阻塞；最终 Go/No-Go 继续由 P0-08 和 P4-08 管理。
