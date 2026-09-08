# P3-08 CPU 性能与容量基线

## 结论

5 个固定 Revision 本地推理模型在 macOS ARM64/CPU 技术预览范围内均通过性能回归下限。ONNX 模型完成 batch 1/8 测量；Qwen2.5 0.5B GGUF 完成真实 llama.cpp 加载和生成测量。当前证据支持桌面、开发和单机低并发场景，不足以推导 Windows/Linux、GPU、CUDA 或生产集群 SLO。

## 测试环境与口径

- .NET `10.0.8`、ONNX Runtime `1.29.0`、llama.cpp `b10516`/`b95502ba9`。
- macOS ARM64，12 个逻辑处理器；所有运行时均使用 CPU，GGUF 设置 `n-gpu-layers=0`。
- 每个 ONNX 场景预热 2 次、正式调用 20 次；GGUF 每轮调用 5 次；完整基准重复 2 轮。
- p50/p95 使用每轮端到端 `InvokeAsync` 延迟的 nearest-rank；汇总取两轮中较高的 p50/p95 和较低的吞吐量。
- ONNX 工作集是同一基准宿主按固定顺序加载/释放模型后的进程级上界；GGUF 工作集来自 llama-server 子进程。

原始两轮样本、环境、阈值和保守汇总保存在 [`p3-08-cpu-macos-arm64.json`](../tests/performance/p3-08-cpu-macos-arm64.json)。

## 保守汇总结果

| 模型 | 场景 | 加载上界 | p95 上界 | 吞吐下界 | 工作集观测上界 |
|---|---:|---:|---:|---:|---:|
| BGE-small-en-v1.5 | batch 1 | 143.7 ms | 7.1 ms | 188.0 items/s | 866.3 MiB* |
| BGE-small-en-v1.5 | batch 8 | — | 30.0 ms | 287.9 items/s | — |
| all-MiniLM-L6-v2 | batch 1 | 60.7 ms | 3.7 ms | 273.2 items/s | 873.0 MiB* |
| all-MiniLM-L6-v2 | batch 8 | — | 25.0 ms | 358.3 items/s | — |
| DistilBERT SST-2 | batch 1 | 135.8 ms | 4.3 ms | 255.0 items/s | 873.4 MiB* |
| DistilBERT SST-2 | batch 8 | — | 20.0 ms | 413.5 items/s | — |
| MobileNet V2 | batch 1 | 11.2 ms | 62.5 ms | 34.1 items/s | 1475.5 MiB* |
| MobileNet V2 | batch 8 | — | 301.8 ms | 40.2 items/s | — |
| Qwen2.5 0.5B Q2_K | batch 1 | 910.3 ms | 163.9 ms | 182.7 completion tokens/s | 662.7 MiB |

`*` ONNX 数字是混合宿主的累计工作集观测值，不是该模型的独立增量。不能将各行相加。

## 技术预览回归门槛

| 模型类型 | 场景 | 最大 p95 | 最低吞吐 | 最大加载 | 最大观测工作集 |
|---|---|---:|---:|---:|---:|
| ONNX 文本 | batch 1 | 250 ms | 4 items/s | 5 s | 2 GiB |
| ONNX 文本 | batch 8 | 1 s | 16 items/s | 5 s | 2 GiB |
| ONNX 图像 | batch 1 | 500 ms | 2 items/s | 5 s | 2 GiB |
| ONNX 图像 | batch 8 | 2.5 s | 4 items/s | 5 s | 2 GiB |
| GGUF 文本生成 | batch 1 | 10 s | 5 completion tokens/s | 10 s | 2 GiB |

这些门槛用于发现明显回归，不是用户可见 SLA。门槛变化必须同时更新基准工具、发布清单和自动证据测试，不能只修改报告文字。

## 容量建议

- 混合 ONNX Worker：技术预览从 3 GiB 内存配额、每个模型一个复用 Session、单 Session 并发 1 开始；在调用层排队，并优先合并到 batch 8。2 GiB 是回归硬上限，不是推荐配额。
- MobileNet：batch 8 没有提高每秒图片数，且 p95 明显增加；低延迟路径使用 batch 1，小批量吞吐场景建议 batch 2～4，batch 8 只用于离线作业。
- 文本 ONNX：batch 8 相比 batch 1 提高总吞吐，适合有短等待窗口的服务端微批处理。
- Qwen GGUF：技术预览从每个 llama-server 1 个活动生成请求、1.5 GiB 内存配额开始；本轮关闭 slots，未验证多请求并发，扩容应优先增加独立进程而不是提高同进程并发。
- 以上建议不包含模型下载缓存、ASP.NET Core 网关、日志缓冲和操作系统页缓存，部署时需要额外预留。

## 未覆盖范围

GPU/CUDA/Metal、Windows、Linux、并发吞吐、长上下文、大图、72 小时稳定性和热降频均未测量。P4-04 已实现有限队列、全局/单模型门禁及估算内存准入，但队列性能不是本报告的测量结论。GPU 策略是“未完成 P0-06 平台矩阵和对应真实基准前一律不通过发布门禁”，不使用 CPU 比例外推 GPU 数字。生产容量必须在目标实例类型上重新执行，并由 P4-07 补齐并发性能与稳定性证据。

## 复验

```bash
dotnet run --project tools/ModelScope.Net.PerformanceBenchmark -c Release -- \
  --artifacts artifacts/certification \
  --llama-executable artifacts/llama.cpp/llama-b10516/llama-server \
  --output tests/performance/p3-08-cpu-macos-arm64.json \
  --iterations 20 \
  --warmups 2 \
  --repeat-runs 2
```

命令只接受本地已下载并校验的固定模型，不执行远程代码，也不把本机绝对路径或提示内容写入报告。
