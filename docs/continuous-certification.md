# P5-01 固定模型持续认证

实现：tools/certification_pipeline.py。登记清单：tests/compatibility/certification-plan.json。

## 执行

在解决方案目录执行：

~~~sh
python3 -m unittest discover -s tools/tests -v
python3 tools/certification_pipeline.py
python3 tools/certification_pipeline.py --model bge-small-en-v1.5
~~~

Python只需标准库（3.11以上）；执行器调用已有.NET认证工具。默认先构建Release；--no-build仅用于已经完成当前构建的工作区。该流程是对已固定Python金标准的重复认证，不会重新生成或批准金标准。

五条登记涵盖BGE、all-MiniLM、DistilBERT分类、MobileNet图像分类、Qwen GGUF生成。每条固定完整Commit、模型及源模型文件SHA/大小、金标准和输入SHA、认证工具类型和报告门槛；GGUF还固定官方分发包及可执行文件SHA。当前GGUF工具仅认证macOS ARM64 CPU，不能据此声称其他平台已通过。

输入快照须提前按登记版本下载至指定目录，包含Hub Manifest；从Manifest取出的身份必须与独立登记的ID/Commit相同，实际文件按登记Hash逐一校验。不存在快照或依赖时失败，不跳过模型。流水线推理子进程不继承Hub Token，也不自动下载模型。

每次生成全新UUID结果目录：artifacts/certification-runs/<run-id>/。各模型保存process.log、真实report.json、result.json；总summary.json从running更新至passed/failed。中断留下running只能表示未完成，不能当作通过；恢复时重新执行，新旧运行不混合。

在运行前后校验输入Hash，并保存/比较源码、项目文件、SDK配置及实际认证工具输出目录的程序集/原生库摘要。执行期文件变化导致失败。子工具退出成功仍须通过报告身份、数值及行为门槛；非有限数值、缺失报告或旧结果目录不能通过。错误子进程超时后清理自身进程组。

## 新模型登记与变更

1. 按当前业务任务选择模型并固定完整Commit及许可证证据。
2. 使用对应Python/ModelScope真实模型生成金标准，保留脚本、输入、环境、输出和独立数值容差审查；不得把当前.NET输出当成金标准。
3. 下载并验证模型和必要源模型；在登记清单中加入独立文件Hash和大小、输入/金标准Hash、适配器和报告门槛。
4. 支持的适配器是embedding、classification、vision和gguf。新任务须先实现相应认证工具和报告检查，再扩展执行器白名单；不能用任意shell命令替代工具。
5. 指定新登记键执行，检查新生成报告、指标和全部制品摘要，再执行全套防误报测试与既有模型复认证。
6. 认证结果只是工程证据；生产Catalog需后续签名/审核流程。P5-03撤销分发以及P0许可证与发布批准并未由本工具代办。

## CI

.github/workflows/model-certification.yml提供手动CI入口，使用专用self-hosted macOS ARM64 runner，标签modelscope-certification。需要预置登记清单要求的只读模型快照和GGUF分发包。clean:false保留预置制品；runner应仅用于受信代码的手动认证，不用于不受信PR或生产服务，不持有生产密钥。

普通ci.yml增加无模型依赖的流水线负面测试。实际模型工作流只声明入口，尚未在远程GitHub runner运行；本机真实模型结果是目前的工程证据。没有可用runner不应伪造CI成功。

更换模型Revision、金标准、工具、SDK/依赖、硬件或影响输出的配置，需要重新运行并保留新证据。上游监控、自动触发和签名撤销控制面分别由P5-02/P5-03完成。
