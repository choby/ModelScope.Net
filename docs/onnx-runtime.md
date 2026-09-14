# ONNX Runtime 原始张量协议

`ModelScope.Net.Runtime.Onnx` 使用 `Microsoft.ML.OnnxRuntime` 1.29.0 在 .NET 进程内执行推理。默认是 CPU；阶段 6 已接入 CUDA、TensorRT、DirectML 和 CoreML 的显式选择与可用性诊断。底层执行器只负责模型会话和张量；Tokenizer、图像处理、标签映射及业务输出由任务适配器实现。

## Execution Provider 与设备

Provider 不会根据操作系统、模型名或 NuGet 包名猜测，必须由应用显式配置。默认配置保持既有 CPU 行为：

```csharp
var runtime = new OnnxRuntimeAdapter(new OnnxRuntimeOptions
{
    ExecutionProvider = OnnxExecutionProvider.Cuda,
    DeviceId = 0,
    AllowCpuFallback = false,
    GpuMemoryLimitBytes = 8L * 1024 * 1024 * 1024,
});

var selection = runtime.GetExecutionProviderSelection();
Console.WriteLine($"{selection.Requested} -> {selection.EffectiveName}");
```

CLI 可先诊断当前进程实际加载的原生库，再执行模型：

```bash
dotnet run --project src/ModelScope.Net.Cli -- onnx providers
dotnet run --project src/ModelScope.Net.Cli -- \
  onnx probe ./models/example --onnx-provider cuda --onnx-device 0
dotnet run --project src/ModelScope.Net.Cli -- \
  run ./models/example --runtime onnx \
  --onnx-provider cuda --onnx-device 0 \
  --onnx-gpu-memory-bytes 8589934592
```

需要允许降级时必须额外传 `--onnx-allow-cpu-fallback`；不要在生产命令中把它作为默认值。

可选值及约束：

| 值 | 原生 Provider 名 | 设备/资源参数 |
|---|---|---|
| `Cpu` | `CPUExecutionProvider` | `DeviceId` 必须为 0；线程由 `IntraOpNumThreads`/`InterOpNumThreads` 控制 |
| `Cuda` | `CUDAExecutionProvider` | 支持非负 `DeviceId` 和可选正数 `GpuMemoryLimitBytes` |
| `TensorRt` | `TensorrtExecutionProvider` | 支持非负 `DeviceId`；TensorRT/CUDA/驱动必须由部署环境提供 |
| `DirectML` | `DmlExecutionProvider` | 支持非负 `DeviceId`；自动关闭 ONNX Runtime memory pattern |
| `CoreML` | `CoreMLExecutionProvider` | `DeviceId` 必须为 0 |

`GetExecutionProviderSelection()` 返回请求值、实际值、请求/实际设备号、ONNX Runtime 原生版本、是否发生进程级 CPU 降级，以及原生库报告的 Provider 列表。CLI 的 `onnx providers` 暴露相同诊断。

本机 2026-09-09 的 `Microsoft.ML.OnnxRuntime` 1.29.0/macOS ARM64 诊断实际列出 `CoreMLExecutionProvider`、`CPUExecutionProvider` 和 `WebGpuExecutionProvider`。当前公开枚举只支持前两者中的 CPU/CoreML；WebGPU 只是原生库附带报告的额外 Provider，不能通过 `OnnxExecutionProvider` 选择。Provider 列表本身只是一份编译注册清单；独立的 `onnx probe` 才会尝试为指定模型创建并释放 Session，但仍不执行输入或完成认证。

“出现在可用列表”只证明 Provider 已编译并注册到当前 ONNX Runtime，不证明它能在当前驱动上初始化、能加载某个模型、所有节点都落在该 Provider，或已经通过认证。必须把以下四层证据分开：

1. `GetAvailableProviders()` 枚举成功；
2. `SessionOptions` 配置与 Provider 初始化成功；
3. 固定模型创建 Session 并完成推理；
4. 固定模型、Commit、制品、平台、Provider/device 和运行时组合完成金标准与资源认证。

### CPU fallback 决策表

| 请求状态 | `AllowCpuFallback` | 实际行为 | 认证与告警 |
|---|---:|---|---|
| 请求 CPU 且 CPU 可用 | 任意 | 使用 CPU | 无 provider 降级告警 |
| 加速 Provider 已列出 | `false` | 使用请求的 Provider，并禁用 ONNX Runtime CPU EP fallback | 不代表已加载或已认证 |
| 加速 Provider 已列出 | `true` | 使用请求的 Provider，但 ONNX Runtime 仍可能把不支持的节点放到 CPU | 每次响应告警“逐节点分配未验证”；`UsedCpuFallback` 仍为 `false`，因为本实现无法观测实际逐节点放置 |
| 加速 Provider 未列出、CPU 可用 | `false` | `Evaluate=Unavailable`；建会话抛 `RuntimeNotInstalled` | fail-closed，无静默降级 |
| 加速 Provider 未列出、CPU 可用 | `true` | 整体改用 CPU，实际设备号为 0 | `UsedCpuFallback=true`；诊断和普通/流式响应均告警；不得继承请求 Provider 的 Certified 状态 |
| 请求与 CPU 均不可用 | 任意 | `Evaluate=Unavailable`；建会话抛 `RuntimeNotInstalled` | fail-closed |

Provider 已列出但配置/初始化失败时抛 `RuntimeNotInstalled`，不会自动转为 CPU；配置成功后，无法安全区分驱动、算子和模型格式导致的 `InferenceSession` 创建失败，因此统一映射为可重试的 `ModelLoadFailed`。推理阶段失败映射为 `InferenceFailed`。

### 原生包与平台边界

| 目标 | ONNX Runtime 官方分发入口 | 本仓库当前状态 |
|---|---|---|
| CPU | [`Microsoft.ML.OnnxRuntime`](https://onnxruntime.ai/docs/install/) | 直接依赖并已在 macOS ARM64/CPU 回归 |
| CUDA / TensorRT | [`Microsoft.ML.OnnxRuntime.Gpu`](https://onnxruntime.ai/docs/install/)；官方 .NET GPU 默认分发当前为 CUDA 12.x，精确 CUDA/cuDNN 组合见 [CUDA EP 要求](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html) | 未作为本包直接依赖，目标原生包、驱动和硬件尚未验证；仍须按冻结版本验证，不能从 CPU 包推断 GPU 兼容性 |
| DirectML | [`Microsoft.ML.OnnxRuntime.DirectML`](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)，Windows | 未直接依赖、未在 Windows 验证；官方已将 DirectML EP 置于持续维护阶段，新 Windows 项目应同时评估 WinML |
| CoreML | [CoreML EP](https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html) 要求 iOS 13+/macOS 10.15+；自定义构建需显式 `--use_coreml` | 当前 macOS ARM64 原生资产已枚举 CoreML；无 CPU fallback 的合成 Add 图已成功建 Session 并得到 `[4,6]`，但尚无固定业务模型、资源、性能或金标准验证 |

本包当前直接依赖 CPU 包，因而“公共 API 可选择加速 Provider”不等于 NuGet 安装后自动具备该 Provider。应用必须按一个冻结的部署组合提供且验证唯一匹配的原生运行库；不能同时混放互斥的 CPU/GPU/DirectML 原生资产后依靠加载顺序碰运气。

当前 API 不枚举物理 GPU/适配器，也不验证某个 `DeviceId` 存在。CUDA/TensorRT 设备号应由 NVIDIA 部署工具确定，DirectML 适配器号由 Windows/DXGI 运维工具确定；把编号传给 `onnx providers` 只验证配置和 Provider 选择，真正的设备有效性必须通过固定模型建 Session/推理验证。

`GpuMemoryLimitBytes` 只传给 CUDA Provider 的 `gpu_mem_limit` 分配器选项。它不是调度准入、操作系统/容器显存配额，也不保证进程总显存不超过该值；TensorRT、DirectML 和 CoreML 当前不接受此选项。

代码当前把 requested/effective Provider、requested/effective Device ID、ONNX Runtime 原生版本和 allow/used fallback 写入候选认证属性，生产 Catalog 要求逐项精确匹配；Catalog 还绑定平台、运行时 SHA-256、模型 Revision 和制品摘要。驱动、CUDA、cuDNN 与 TensorRT 版本尚未由运行时代码自动采集，必须进入目标环境证据并在加速组合批准前补齐，不能声称生产 GPU Certified。

## 请求格式

`ModelRequest.Payload` 可以直接是按 ONNX 输入名组织的对象，也可以包在 `inputs` 中。嵌套数组会自动推断形状：

```json
{
  "inputs": {
    "input_ids": [[101, 7592, 102]],
    "attention_mask": [[1, 1, 1]]
  }
}
```

需要使用扁平数据时，可显式提供 `data` 和 `shape`：

```json
{
  "input": {
    "data": [1.0, 2.0, 3.0, 4.0],
    "shape": [2, 2]
  }
}
```

当前支持 `float32`、`float64`、`int64`、`int32`、`bool` 和字符串张量。数组必须为矩形，实际形状必须符合模型的固定维度；动态维度由 ONNX 元数据中的负数表示。

## 响应格式

输出保留名称、元素类型、实际形状和扁平数据，供任务适配器零歧义处理：

```json
{
  "outputs": {
    "logits": {
      "type": "Single",
      "shape": [1, 2],
      "data": [0.1, 0.9]
    }
  }
}
```

ONNX 图本身不提供增量生成语义，因此流式接口返回一个终止 `result` 事件。生成模型需要后续任务适配器实现逐 token 调度。

## 选择和资源限制

- 默认优先选择名为 `model.onnx` 的制品，否则按路径稳定排序选择第一个；可通过 `OnnxRuntimeOptions.ModelFile` 指定。
- 制品路径必须位于已下载模型目录内，禁止 `..` 越界。
- `MaxInputElements` 默认限制单个输入为 16M 元素。
- `MaxConcurrentRuns` 默认每个会话同时执行一个请求。
- `IntraOpNumThreads` 和 `InterOpNumThreads` 可限制 CPU 线程；零值交由 ONNX Runtime 决定。

生产模式仍只应路由兼容性 Catalog 中已固定 Model ID、Revision、ONNX 文件和任务适配器的模型。
