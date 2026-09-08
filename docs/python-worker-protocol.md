# Python Worker 协议与运行说明

生产主链路采用 gRPC，契约源文件为 `src/ModelScope.Net.Runtime.Python/Protos/python_worker.proto`。它覆盖健康检查、模型加载、普通调用、服务端流式调用和会话卸载；C# 客户端由构建过程生成，Python 服务端绑定代码提交在 `worker/python/generated` 中。

## gRPC 方法

| 方法 | 用途 | 关键结果 |
|---|---|---|
| `Health` | 就绪与版本检查 | `status`、`version`、诊断消息 |
| `LoadModel` | 从 .NET 已下载的本地目录加载固定模型 | `session_id`、警告 |
| `Invoke` | JSON 请求/响应调用 | JSON 输出、警告 |
| `InvokeStreaming` | 服务端流式 JSON 事件 | 事件名、JSON 数据、终止标志 |
| `UnloadModel` | 最佳努力释放会话 | 空响应 |

协议依赖固定为 .NET `Grpc.Net.Client`/`Grpc.Tools` 2.83.0、`Google.Protobuf` 3.36.1，以及 Python `grpcio`/`grpcio-tools` 1.83.1、`protobuf` 7.36.1。修改 proto 后重新生成 Python 绑定：

```bash
python3 -m venv .worker-venv
.worker-venv/bin/python -m pip install -r worker/python/requirements-dev.txt
.worker-venv/bin/python -m grpc_tools.protoc \
  -I src/ModelScope.Net.Runtime.Python/Protos \
  --python_out=worker/python/generated \
  --grpc_python_out=worker/python/generated \
  src/ModelScope.Net.Runtime.Python/Protos/python_worker.proto
```

## 启动 Worker

基础依赖只包含协议运行库。`echo` 后端用于协议验收，不执行实际模型：

```bash
.worker-venv/bin/python -m pip install -r worker/python/requirements.txt
.worker-venv/bin/python worker/python/server.py --port 50051 --backend echo \
  --model-root /srv/modelscope/models --api-key-file /run/secrets/modelscope-worker-key
```

`modelscope` 后端延迟导入 `modelscope.pipelines.pipeline`，因此需按待认证模型的任务域安装 ModelScope 及其推理依赖，再启动：

```bash
python worker/python/server.py --port 50051 --backend modelscope \
  --model-root /srv/modelscope/models --api-key-file /run/secrets/modelscope-worker-key
```

模型依赖不统一，不能放入一个无上限的基础镜像；它们应由兼容性 Catalog/发布清单按模型族固定。Worker 接收的是 .NET Hub 已下载并校验的本地模型目录，不再次决定 Model ID 或 Revision。

ASP.NET Core 选择 gRPC 传输的核心配置如下：

```csharp
services.AddModelScopeNet(configurePython: options =>
{
    options.Endpoint = new Uri("http://127.0.0.1:50051");
    options.Transport = PythonWorkerTransport.Grpc;
});
```

若由宿主托管本地进程，可同时提供 `IPythonWorkerSupervisor` 工厂。HostedService 负责随应用启停 Worker；运行时在瞬时健康检查/加载失败后执行有限次数重启恢复。

## 安全边界

- `AllowRemoteCode` 默认关闭；能力检测发现远程代码时客户端拒绝路由。
- 允许远程代码时，gRPC `LoadModel.allow_remote_code` 会传给 ModelScope `pipeline(..., trust_remote_code=True)`。
- Worker 将 `parameters` 中的 `true`/`false`/`null`、整数、浮点和 JSON 对象/数组转成 Python 类型后再交给 pipeline。
- Worker 在加载时再次扫描 `.py` 和 `requirements.txt`，防止能力元数据漏报。
- 本地进程使用参数列表启动，不经过 Shell，并限制保留的诊断行数。
- 本机 Supervisor 强制 IP 回环、随机 Bearer Key、模型根目录白名单、环境变量白名单和请求/会话上限；诊断默认关闭。
- 非回环明文 Endpoint 会被客户端拒绝；非回环 HTTPS 还必须配置客户端证书、私钥与受信 CA。P4-05 已提供真实 mTLS 握手测试，以及容器隔离、只读模型挂载、默认拒绝网络和 CPU/GPU/内存/PID 配额清单。
- 模型输入可能包含敏感数据；日志和健康检查不得记录请求正文或 Token。

完整控制与未完成项见 [Worker 安全隔离](./worker-security.md)。

## HTTP 兼容传输

早期 HTTP/SSE 预览客户端仍保留用于兼容与对照测试：`GET /health`、`POST /v1/models/load`、`POST /v1/sessions/{id}/invoke`、`POST /v1/sessions/{id}/stream`、`DELETE /v1/sessions/{id}`。新部署应选择 `PythonWorkerTransport.Grpc`；HTTP 不再作为生产协议演进主线。

## 验收

`GrpcPythonWorkerRuntimeTests` 会启动真实 Python 子进程并验证健康、加载、调用、流式、卸载，以及强制终止后的自动拉起恢复。CI 通过 `MODELSCOPE_WORKER_PYTHON` 指定已安装基础依赖的 Python 解释器。
