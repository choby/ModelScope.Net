# P4-05 容器与部署基线

P4-05 提供 Linux CPU、Linux NVIDIA GPU 和 Windows CPU 三类部署策略，并关闭 P4-03 安全矩阵剩余的文件系统、网络、OS 沙箱、资源配额和跨主机 mTLS 控制。机器可读结论位于 `deployment/p4-05-deployment-profile.json`，Kubernetes 清单位于 `deployment/kubernetes`。

这里的“控制通过”表示代码、清单和自动化验收已具备并采用 fail-closed 默认值。某个生产集群仍必须证明其 CNI、容器运行时、Kubelet PID 限制、证书、PVC 和 GPU 插件确实生效；这属于部署前门禁和 P4-07 长稳验证，不能只凭仓库中的 YAML 推定。

## 平台策略

| 平台 | 本地运行范围 | 状态 |
|---|---|---|
| Ubuntu 24.04 x64 CPU | 隔离 Python Worker、认证 ONNX/GGUF | Linux CPU 清单已验证 |
| Linux x64 NVIDIA GPU | 模型专用 GPU Worker，独占一个 GPU | GPU 清单已验证；真实硬件认证仍 fail-closed |
| Windows Server 2025 x64 CPU | 认证 ONNX 与 Remote；Python 经 mTLS 转交 Linux | 策略已验证，本地 Python/未知代码明确拒绝 |

Windows 首期不在本机执行 Python、TrustedModel 或 IsolatedUntrusted 代码。这不是功能缺失的静默降级，而是 `deployment/windows-cpu/execution-policy.json` 中的显式拒绝策略。

## 镜像构建门禁

`deployment/containers/python-worker/Dockerfile` 的默认基础镜像指向不可访问的 `registry.invalid`。构建系统必须覆盖它并传入带 SHA-256 digest 的批准基础镜像：

```bash
docker build \
  --build-arg PYTHON_BASE_IMAGE='<approved-python-image>@sha256:<digest>' \
  -f deployment/containers/python-worker/Dockerfile \
  -t '<registry>/modelscope-net/python-worker:<version>' .
```

该镜像只包含 Worker 协议依赖。生产模型镜像必须从它派生，安装经锁定和扫描的 ModelScope/Transformers/任务依赖，然后由发布流水线生成 SBOM、扫描、签名并推送。基础清单使用 `registry.invalid` 和全零 digest，故在发布系统替换为批准的签名 digest 前无法被误部署。

本机已使用固定的 `python:3.14-alpine3.24` Linux/ARM64 digest 构建协议基础镜像。最终镜像以 uid/gid 65532 运行，移除运行期不需要的 pip，Docker Scout 对 43 个包的 Critical/High/Medium/Low 扫描均为 0；受限运行测试验证只读根目录、只读模型卷、无网络、0.5 CPU、256 MiB 内存、64 PID 和限额 `/tmp` 均实际生效。完整摘要见 `deployment/container-build-evidence.json`。该证据不替代 Linux x64/GPU 或模型专用派生镜像的重新构建与扫描。

模型文件不得在 Worker 启动时联网下载。发布流水线先使用 Hub 客户端下载固定 Revision 并校验，再写入只读 `modelscope-models` PVC 或不可变镜像层。

## Kubernetes 强制控制

基础清单强制：

- 根文件系统只读，模型 PVC 只读；仅 `/tmp` 使用 1 GiB `emptyDir`。
- uid/gid 65532 非 root，禁止提权和特权模式，删除全部 capabilities，使用 `RuntimeDefault` seccomp。
- ServiceAccount 不挂载集群令牌，命名空间启用 restricted Pod Security。
- NetworkPolicy 默认拒绝全部入口和出口，只允许 `modelscope-system` 命名空间中带网关标签的 Pod 访问 50051 mTLS 端口。
- Pod 和命名空间同时限制 CPU、内存及临时存储；GPU 覆盖层限制 `nvidia.com/gpu: 1`。
- 专用 Worker 节点必须合并 `node-policy/linux-worker-kubelet.yaml`，把 `podPidsLimit` 设置为 256。

渲染检查：

```bash
kubectl kustomize deployment/kubernetes/overlays/linux-cpu
kubectl kustomize deployment/kubernetes/overlays/linux-gpu
```

部署前必须提供：

- `modelscope-models` 只读 PVC；
- `modelscope-worker-server-tls` Secret，包含 `tls.crt`、`tls.key`、`ca.crt`；
- `modelscope-worker-api-key` Secret，包含 `api-key`；
- 已启用 NetworkPolicy 的 CNI；
- 应用 `podPidsLimit` 的专用节点；
- GPU 场景的 NVIDIA device plugin 和 `nvidia` RuntimeClass。

## 跨主机 mTLS

Python Worker 只有在同时提供服务端证书、私钥和客户端 CA 时才允许绑定非回环地址。服务端使用 `grpc.ssl_server_credentials(..., require_client_auth=True)`；API Key 继续作为第二层应用身份。

.NET 远程 Worker 同样采用 fail-closed：非回环 HTTPS Endpoint 缺少客户端证书、私钥或 CA 中任一项都会在运行时构造阶段失败。

```csharp
services.AddModelScopeNet(configurePython: options =>
{
    options.Endpoint = new Uri("https://modelscope-python-worker.modelscope-runtime.svc:50051");
    options.Transport = PythonWorkerTransport.Grpc;
    options.Tls.ClientCertificatePath = "/run/modelscope/tls/client.crt";
    options.Tls.ClientPrivateKeyPath = "/run/modelscope/tls/client.key";
    options.Tls.TrustedCaCertificatePath = "/run/modelscope/tls/ca.crt";
});
```

客户端验证服务端主机名和自定义 CA 链；真实集成测试确认带客户端证书可以完成健康检查和推理，没有客户端证书则在 TLS 握手阶段失败。证书必须由短周期内部 CA 或工作负载身份系统轮换，不能烘焙进镜像或仓库。

## 发布前检查

### 2026-09-06 本机复验

再次确认 Docker 启动后，当前镜像 12 项隔离复验通过，报告 `artifacts/container-checks/870c46434797409aa3c9b6edbc1d65a7/report.json`；部署安全和真实 mTLS 专项 6 项通过，证据 `artifacts/acceptance/docker-resumed/`。当前镜像 Docker Scout 漏洞扫描因可能向外部服务发送包/依赖元数据，被自动安全审核拒绝，需用户明确授权后执行，尚无新扫描结论。

Docker 29.7.2 已可连接。复验发现旧 `p4-05-local` 镜像内 server.py 与当前源码不同；保留旧镜像，使用原固定基础 digest 重建为 `modelscope-net/python-worker:p5-current-local`，镜像 ID 为 `sha256:b54aa6dda3ece8618b3f76a61f724deba063fec7ec4f33b81d3eca13e7703fd6`。

执行 `python3 tools/container_isolation_check.py --image modelscope-net/python-worker:p5-current-local`：12 项检查通过，包括只读根/模型卷、可写临时目录、无启用的非回环接口且外连失败、uid/gid 65532、禁止提权、seccomp filter、零 effective capabilities、实际 cgroup CPU/内存/PID 配额及 Worker 源码哈希一致性。报告：`artifacts/container-checks/907c16c47d744c2aac6cf8d2f07c22d2/report.json`。

首次失败报告 `245ac1e0819341fab7b1ec9438a5d33e/report.json` 保留：旧镜像确有源码漂移；网络项同时存在探针误判，修正为忽略未启用的隧道接口后复验。脚本仅清理自己创建的临时容器。此次覆盖本机 Linux/ARM64 内核隔离，不启动模型推理、不验证 mTLS，不测 OOM/PID 耗尽，不替代 Kubernetes CNI、x64/GPU、72 小时稳定性和新镜像漏洞扫描。历史扫描结果不能直接视为新镜像的扫描结果。

1. 将占位镜像替换为扫描并签名的 digest，拒绝 tag-only 镜像。
2. 在目标集群执行服务端应用、Pod Security、NetworkPolicy、只读挂载和资源限制检查。
3. 从非网关 Pod 验证 Worker 端口不可达，从 Worker 验证外网不可达。
4. 验证匿名 TLS、错误 CA 和过期证书均被拒绝。
5. 验证 OOM、PID 上限和 GPU 配额只终止 Worker，不影响网关。
6. 将结果写入 P4-07 稳定性证据；未完成时不得将 Linux GPU 或生产集群标记为运行认证通过。
