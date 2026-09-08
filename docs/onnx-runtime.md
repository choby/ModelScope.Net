# ONNX Runtime 原始张量协议

`ModelScope.Net.Runtime.Onnx` 使用 `Microsoft.ML.OnnxRuntime` 1.29.0 在 .NET 进程内执行 CPU 推理。底层执行器只负责模型会话和张量；Tokenizer、图像处理、标签映射及业务输出由 P3-02～P3-04 的任务适配器实现。

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
