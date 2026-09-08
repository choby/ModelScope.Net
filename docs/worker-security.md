# P4-03 Worker 安全隔离

P4-03 将 Python Worker 与 llama-server 的“受控子进程”收紧为应用层隔离，P4-05 又补齐生产容器控制。机器可读矩阵位于 [`deployment/worker-security-matrix.json`](../deployment/worker-security-matrix.json)，当前 16 项控制全部通过，P4-03 已完成。该结论只适用于按 [P4-05 部署基线](./deployment-p4-05.md) 落地的环境；脱离清单直接执行任意第三方模型代码仍不能视为已沙箱化。

## 已强制的控制

### 子进程与环境

- `UseShellExecute=false`，参数通过 `ArgumentList` 传递，不拼接 Shell 命令。
- Host、模型路径、模型根目录、Key 文件以及 Web UI/slots 等安全关键参数由 Supervisor 独占，重复覆盖会在启动前失败。
- 默认清空父进程环境，仅继承 PATH、区域设置、临时目录和 Windows 启动所需变量。
- 名称包含 Token、Secret、Password、API Key、Authorization、Cookie、Credential 或 Private Key 的显式变量默认拒绝转发。
- Python 强制 `PYTHONNOUSERSITE=1`、`PYTHONDONTWRITEBYTECODE=1`，减少用户级包和模型目录写入。
- stdout/stderr 默认不保存。显式开启时执行凭据模式脱敏、控制字符替换、单行长度限制和总行数限制。
- 崩溃恢复、重启和宿主退出均回收完整进程树。

如果确实需要向受信任 Worker 传递敏感环境变量，必须显式设置 `Security.AllowSensitiveEnvironmentVariables=true`。这会扩大泄漏面，不属于当前认证配置。

### 网络与身份

- 托管 Python Worker 与 llama-server 只允许绑定 IP 回环地址。
- 本机 Python gRPC Worker 使用每个 Supervisor 随机生成的 256-bit Bearer Key。
- Key 只通过用户可读写的临时文件传给 Worker，不进入命令行或环境变量；Supervisor 释放时删除。
- Python HTTP/gRPC 客户端拒绝非回环的明文 HTTP Endpoint；远程 Worker 必须使用 HTTPS。
- llama-server 延续随机 API Key、回环绑定、禁用 Web UI 和 slots 的策略。

### 模型与容量边界

- Python Worker 至少配置一个 `--model-root`，解析后的模型路径必须位于允许根目录内。
- `AllowRemoteCode` 仍默认关闭；Worker 端再次扫描 `.py` 和 `requirements.txt`。
- Python Worker 默认最多 4 个会话、4 个执行线程和 4 MiB 请求正文；参数有硬上限。
- 宿主 `ModelSessionPool` 默认限制 5 个缓存 Session、3 GiB 估算内存、1 个全局/单模型活动租约及 32 个排队请求；活动和待授予租约不会被驱逐。
- Worker 返回的异常只公开稳定类型，不回传第三方异常正文或本地路径。

本机 Supervisor 的典型配置：

```csharp
var worker = new LocalPythonWorkerProcessOptions
{
    PythonExecutable = ".worker-venv/bin/python",
    WorkerScript = "worker/python/server.py",
};
worker.Arguments.Add("--port");
worker.Arguments.Add("50051");
worker.Arguments.Add("--backend");
worker.Arguments.Add("modelscope");
worker.AllowedModelRoots.Add("/srv/modelscope/models");

var supervisor = new LocalPythonWorkerSupervisor(worker);
```

`LocalPythonWorkerSupervisor` 自动追加回环 Host、Key 文件和模型根目录参数。`GrpcPythonWorkerRuntime` 从 Supervisor 的安全上下文取得 Key，调用方不需要读取或复制它。

## 自动验收

自动化测试使用真实 Python 子进程和 gRPC Worker 验证：

- 父进程哨兵秘密未出现在 Python/llama 子进程；
- 敏感显式环境变量启动失败；
- 非回环绑定和远程明文 Endpoint 被拒绝；
- 无 Bearer Key 的健康检查返回 `Unauthenticated`；
- Key 文件在 Unix 上为 `0600`，Supervisor 释放后不存在；
- 模型根目录越界、会话超限和请求超限返回结构化错误；
- 诊断内容中的 Bearer、Token 和 ModelScope Token 被脱敏；
- 崩溃恢复后使用新进程，停止后无子进程残留。

## 生产部署强制项

以下控制已由 P4-05 清单实现，部署环境必须全部启用：

- 只读模型挂载和独立可写临时目录；
- 默认拒绝外网访问的网络命名空间或 NetworkPolicy；
- 非 root 用户、capability drop、`no-new-privileges` 和 seccomp/AppArmor；
- 操作系统级 CPU、内存、进程数、文件句柄和 GPU 配额；
- 跨主机 Worker 的 mTLS 双向身份。

Linux Worker 通过只读 PVC、默认拒绝网络、restricted Pod Security、CPU/内存/临时存储/GPU/PID 配额和 mTLS 实施这些要求。Windows CPU 首期明确拒绝本地 Python 与未知模型代码，并经 mTLS 转交 Linux。对含自定义 Python 代码的未知模型，即使显式打开 `AllowRemoteCode`，仍必须使用模型专用签名镜像或更强的一次性沙箱，不能落入通用 Worker。
