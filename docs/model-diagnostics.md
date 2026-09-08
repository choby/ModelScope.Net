# FR-06 模型诊断：当前能力与验收边界

`ModelInspector.InspectAsync` 的返回值新增可选 `Diagnostics` 属性；既有ModelCapabilities构造参数保持不变，CLI `inspect` 自动输出此字段。

当前提取内容：

任务字段优先采用显式配置。缺少任务时，仅在所有声明架构都能映射且映射一致时推断：ForSequenceClassification→text-classification、ForImageClassification→image-classification、ForCausalLM→text-generation，并在Warnings保留推断提示。通用编码器、seq2seq、多义/未知或冲突架构不猜测；这不是任务认证，不能据此认为模型已通过加载/数值验证。生产仍要求签名Catalog、固定制品及运行时匹配，CLI显式 --task 可覆盖检测结果。

目录扫描采用 `ModelInspectionLimits`，默认 MaximumEntries=100000（文件与目录条目合计，包括遇到的忽略目录自身）、MaximumDirectoryDepth=64（根目录为0层），可通过 `new ModelInspector(new ModelInspectionLimits(...))` 显式配置。枚举前及每个条目检查取消，超过数量/深度抛出 InvalidRequest，不返回部分能力结果。根目录 .git/.modelscope 在递归前剪枝；其他目录符号链接/重解析点拒绝遍历，文件符号链接保留以兼容 Blob 缓存。诊断复用受限扫描得到的根文件列表，不再独立重复枚举目录。此处并非OS沙箱或抗并发路径替换保证：应在不被并发修改的可信快照上运行，文件链接目标仍需部署侧控制。

- 根目录config.json、configuration.json、model_index.json中的简单license标识，以及LICENSE/许可证文件存在性；每项附来源，始终RequiresReview=true，不推断法律批准或商业可用性。
- 根目录 README.md、readme.md、README.MD 的闭合 front matter 中顶层 license 标识或简单平面列表；支持引号/键周围空格、BOM/CRLF、标量行尾注释，附来源行号。正文或嵌套 license 不作为顶层声明。重复键（包括不同引号/空格写法）、标签/别名、URL、块标量、嵌套列表、不闭合或超限文档不返回该 README 的部分许可证结论，提示人工复核且不回显原文。
- requirements.txt中的简单Python包名和版本约束，附行号；不安装、不导入、不执行、不跟随-r、URL或其他指令。不支持的声明提示人工复核而不输出原文，避免携带URL凭证。声明不等于依赖已安装或版本兼容。
- 已识别制品的字节总量；RequiredRamBytes与RequiredVramBytes为null，不能把磁盘文件大小当运行资源需求。

诊断读取每个元数据文件限制256 KiB。原有核心配置解析现在另外限制每个 configuration.json/config.json/model_index.json 为1 MiB、快照清单为16 MiB；打开文件后检查实际内容长度，并对后续读取计数，防止缓存符号链接长度或文件增长绕过。超限抛出 InvalidRequest，不返回不完整能力结论。损坏或非对象配置使 ContainsRemoteCode 保守置true，并提示远程代码状态未知、需显式审核；这不是确认存在远程代码。损坏或非对象清单不提供模型身份。JSON解析警告不附带原始异常消息。全目录扫描/文件数量、其他读取入口及OS异常尚未全面限额或脱敏。

未找到许可证或依赖文件时说明未知，而不视为无需依赖/无限制许可证。README 解析是明确限定的非执行子集，不是通用 YAML 解析器或整份 YAML 的有效性验证；不支持转义字符串、合并键、多行流式列表或完整 SPDX 表达式。Python环境标记/extras、pyproject/环境锁、传递依赖解析或实际资源剖析仍待完成。

新增测试覆盖声明与来源、指令不执行、敏感URL不回显、许可证需审核、RAM/VRAM未知、超限要求人工复核。全套186项通过（Hub43 + Runtime129 + ASP.NET9 + Cli5），0跳过；证据 `artifacts/acceptance/fr06-diagnostics/`。

2026-09-07 README 增量：新增19个案例，包含简单声明/列表、来源、重复键、标签/别名/URL拒绝、嵌套/正文忽略、超限和未闭合行为；完整 Hub43 + Runtime154 + ASP.NET9 + Cli5 =211项通过，0失败/0跳过，证据 `artifacts/acceptance/fr06-readme-keys/`。上一版完整208项证据保留在 `artifacts/acceptance/fr06-readme-license/`。

FR-06保持部分完成：完整依赖来源覆盖、已部署运行环境匹配、复杂许可证表达与真实资源需求仍需补齐；许可证批准仍属于P0-07，不会由检测器自动代替。

2026-09-07 核心元数据限额：新增10项案例覆盖四种超限文件、配置/清单精确上限、损坏内容不回显及非对象配置保守处理。完整 Hub43 + Runtime164 + ASP.NET9 + Cli5 =221项通过，0失败/0跳过，`artifacts/acceptance/fr06-metadata-final/`；此目录final指本轮回归，不是项目最终验收。

2026-09-07 目录扫描增量：6项新增案例覆盖条目边界、深度/忽略目录剪枝、目录链接循环拒绝、文件链接实际内容大小和README诊断、预取消及无效限额。完整 Hub43 + Runtime170 + ASP.NET9 + Cli5 =227项通过，0失败/0跳过，`artifacts/acceptance/fr06-scan-limits/`。实际 DistilBERT 固定快照 CLI inspect 退出0，ID/Commit/架构及两模型制品总计535788269字节正确恢复；不代表重新完成推理认证。

2026-09-07 任务推断增量：8项新增案例验证明确后缀、未知/冲突保留未知、显式配置优先及推断提示。完整 Hub43 + Runtime178 + ASP.NET9 + Cli5 =235项通过，0失败/0跳过，`artifacts/acceptance/fr06-task-inference/`。真实CLI在不传 --task/--runtime 时，对固定DistilBERT输入“I absolutely loved this movie.”选择 onnx-text-classification，返回POSITIVE、score=0.9998777，退出0；这是单条衔接冒烟，不替代完整金标准复认证。
