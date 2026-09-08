# 真实 ModelScope Python Worker：准备与验收记录

状态：IN_PROGRESS；本机 .NET → gRPC → 原 ModelScope pipeline → DistilBERT 的固定模型标签/概率及卸载清理验证通过。生产依赖锁、其他任务/平台及容器模型认证未完成。下文保留历史检查点，历史失败不表示当前仍失败。

## .NET 真实 gRPC 最新证据

### 环境追溯增量

`artifacts/worker-certification/2415ed1585e3442e9b02b71f751f265d/report.json` 在清理依赖元数据歧义后重新完成普通/流式/新会话恢复及进程清理验证。该报告通过 SHA-256 绑定同目录 `environment.json`，包含解释器版本/二进制摘要、OS/架构、6 个关键模块实际版本/元数据版本/入口文件摘要、当前选中安装包的版本和 METADATA 摘要，以及原项目 modelscope 目录 2,853 个 Python 文件的逐文件及聚合摘要。环境探针与 Worker 使用相同的显式隔离环境；探针失败会阻止进入模型验证。

新增 `worker/python/validation_environment.py` 验证独立依赖层没有重复包元数据、实际模块版本与选中元数据相符，以及当前平台未启用 extras 的依赖约束全部满足。6 项专项测试覆盖成功/源码变化、重复元数据、模块版本不符、缺失依赖、版本不符和未启用 extra；运行 `PYTHONDONTWRITEBYTECODE=1 .certification-venv/bin/python -m unittest discover -s worker/python -p test_validation_environment.py -v` 全部通过。

依赖修正：独立层 attrs 明确重装声明版本 25.4.0；陈旧的 attrs 26.1.0、huggingface_hub 1.30.0 两个 `.dist-info` 目录移至 `artifacts/worker-dependency-quarantine/`，可恢复，未删除模型或修改金标准虚拟环境。实际模块/元数据确认 torch 2.9.1、transformers 4.57.6、datasets 4.8.4、huggingface_hub 0.36.2、grpc 1.83.1、attrs 25.4.0，约束错误为零。

这仍不是生产锁或完整 SBOM：没有冻结 wheel 来源/下载哈希，没有哈希全部第三方包内容，也不包含原项目非 Python 资源；只检查选中的已安装包及当前平台/未启用 extras 约束。源码快照不能替代生产签名镜像，环境报告也不等于漏洞扫描。

### 流式与故障恢复增量

报告 `artifacts/worker-certification/74418e49cd6442da81e814b0e4d26ac9/report.json` 通过：同一固定 DistilBERT 完成普通调用、8 条 data 事件和唯一 done/null 结束事件的流式调用、卸载后强制终止自有 Worker、由原 .NET runtime/supervisor 在新建会话时自动恢复并重新加载真实模型。普通、流式及恢复后均为 8/8 标签一致、最大概率误差约 1.1921e-7；报告记录旧/新 PID 及最终全部自有进程已退出。工具三分钟总取消预算，清理失败也会尝试写失败报告，异常输出仅保留类型。

这是分类结果的 gRPC 流式传输：原 ModelScope 后端先完成整批推理，再逐条发送结果，并非 token 级增量生成或首 token 延迟认证。故障注入发生在前一会话卸载后，证明新会话恢复；不代表执行中请求无损重放、旧会话自动恢复或宿主进程崩溃恢复。首轮 `877b70c123e14a04969310a3aff02fa4` 的流式已通过，但验证工具读取按 PID 重开进程的退出码失败；改为跨平台的实际退出检查后复验通过。未修改生产 Worker/Runtime 的执行语义。

### 普通调用基线

工具：`tools/ModelScope.Net.WorkerCertification`，已加入解决方案。复跑：

```bash
dotnet run --project tools/ModelScope.Net.WorkerCertification -c Release -- /Users/choby/Documents/github/ModelScope.Net /Users/choby/Documents/github/modelscope
```

报告 `artifacts/worker-certification/f04a8f0c118c4ad0a639208ba57b8906/report.json`：固定 ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f，先核对五个制品的实际内容大小/SHA-256及 gold 身份/哈希，再由 .NET 管理子进程并使用真实 gRPC 客户端调用。8/8 标签一致，最大概率绝对误差 1.1920928955078125e-7，小于既有计划概率阈值 1e-4。卸载后健康检查通过，进程 86869 在释放后已退出。原 pipeline 自动选用 mps:0，未强制 CPU。报告包含实际输出、制品/gold/Worker 哈希。首轮工具把符号链接自身长度当作内容长度，故制品预检失败；改为打开文件获取内容长度后通过，失败报告 333c51b807ff42f38fc3bab2c7189f0c 保留。

范围限制：pipeline 输出概率而非 logits，报告明确 logitsCompared=false；不是全项 ONNX 认证替代。普通调用基线不覆盖流式/恢复，后续增量仅按上文限定场景补验；完整环境/原源码树摘要、容器运行、生产依赖锁与漏洞扫描仍待完成，也未证明所有原项目模型兼容。工具使用显式受控 PYTHONPATH；未开启继承父进程全部环境或敏感变量例外。

## 历史准备检查点

最新检查点：已对独立 `.worker-model-deps` 执行声明版本 huggingface-hub==0.36.2 的无传递依赖更新；实际模块和 metadata 均返回 0.36.2，Transformers 4.57.6 和原源码 pipeline 导入成功。通过 server.ModelScopePipelineModel 直接加载固定 DistilBERT 本地快照（ef2f51c8f6a09fb8c418d1ccbda386970ed2d53f），禁用 Hugging Face 联网，传入 truncation=true、max_length=128、top_k=null；8 条输入的最高概率标签与已有 gold 全部一致。运行日志显示自动设备为 mps:0。此处只证明 Python 后端实际执行与标签一致，未做数值容差比较、未固定 CPU、未经过 .NET gRPC，也不是完整认证。下一步由专用 .NET 验证入口显式配置依赖环境，覆盖真实协议、数值输出与子进程清理，保存完整可复跑报告。

2026-09-06 恢复核验：上轮安装进程句柄已不存在；独立依赖层中 datasets 4.8.4 和 huggingface_hub 1.30.0 已落盘。禁止字节码写入且开启离线模式的实际导入确认 Transformers 拒绝后者，要求 huggingface-hub >=0.34.0,<1.0。datasets 要求 >=0.25.0,<2.0，因此声明中的 0.36.2 满足双方约束，但尚未完成依赖层修正。不得升级或覆盖冻结金标准环境。CLI 管理子进程使用隔离环境，不应假定父进程 PYTHONPATH 会继承；验证工具需显式配置受控依赖路径，保留默认秘密环境过滤。

2026-09-06核对：`.worker-venv`只有协议依赖，没有modelscope/torch/transformers；`.certification-venv`具有torch2.9.1和transformers4.57.6，但没有grpcio。原项目源码版本2.0.0+main的pipeline导入首先缺addict，补充后需要datasets。原源码组件索引已成功扫描991项。

实测采用`.certification-venv/bin/python`加独立`.worker-model-deps`依赖层和原项目源码路径，不修改既有金标准环境；额外依赖声明为 `worker/python/model-validation-requirements.txt`。MODELSCOPE_CACHE显式指向专用依赖目录下cache，避免写入通用用户缓存；PYTHONDONTWRITEBYTECODE=1。第一次默认缓存位置触发权限失败，不计为导入成功。

后续恢复顺序：完成datasets依赖安装；按声明校正huggingface-hub至0.36.2（datasets默认解析到1.x，与现有transformers不兼容）；验证完整pipeline导入；以已下载DistilBERT分类快照启动真实modelscope后端；由.NET gRPC调用并与既有Python gold核对，保存模型/依赖/源码哈希及输出报告。依赖安装、组件索引生成不是端到端认证。

专用依赖目录当前尚非生产环境锁：传递依赖未完整冻结、运行兼容性与漏洞扫描待验；生产模型镜像必须独立锁定、构建和认证，不能引用本机源码PYTHONPATH作为生产部署方式。
