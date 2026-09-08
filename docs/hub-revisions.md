# FR-01 版本列表与固定提交：验收状态

2026-09-06：新增 `IModelScopeHubClient.GetModelRevisionsAsync(modelId)` 与 CLI `revisions owner/model`。返回 `HubModelRevision(Name, Kind, CreatedAt)`，区分 Branch/Tag，保留同名分支和标签；重复同类名称与错误响应结构拒绝。时间戳支持秒和毫秒，缺失或越界记为未知，不推断 Commit。

实现对照当前原项目 `modelscope/hub/api.py`：该文件委托给官方 `modelscope_hub.compat.LegacyHubApi`。官方实现通过 `/api/v1/models/{owner}/{name}/revisions` 返回 `Data.RevisionMap.Branches/Tags`，见 [官方底层客户端](https://github.com/modelscope/modelscope_hub/blob/main/src/modelscope_hub/_legacy_api.py)。兼容层还具有按SDK发布时间选择标签的规则，见 [官方兼容层](https://github.com/modelscope/modelscope_hub/blob/main/src/modelscope_hub/compat/hub_api.py)；当前.NET没有宣称复制该规则。

真实.NET命令 `dotnet run --project src/ModelScope.Net.Cli -c Release -- revisions BAAI/bge-small-en-v1.5` 返回一个master分支，CreatedAt为2024-09-13T05:21:21+00:00，无标签。该版本列表没有仓库头Commit，因此此API明确只查询名称，不进行固定提交解析。

Hub23项测试通过，新增5个版本案例；全套155项通过（Hub23、Runtime118、ASP.NET9、Cli5），证据：`artifacts/acceptance/fr01-revisions-full/`。随后仅增加Branch/Tag可读JSON枚举输出并再次通过真实查询。

## 固定提交解析与下载（2026-09-06）

已新增 `ResolveModelRevisionAsync` 与 `resolve owner/model --revision REV`。实现纯.NET读取Git Smart HTTP v0引用公告，不调用系统Git；请求使用HTTP/1.1和明确的 `git/ModelScope.Net` 标识，响应限制4 MiB，按pkt-line字节长度解码，错误媒体类型、截断/重复/无效内容拒绝。协议依据：[Git HTTP引用发现](https://git-scm.com/docs/gitprotocol-http)、[Git传输协议](https://git-scm.com/docs/gitprotocol-pack)。

支持分支、轻量标签和附注标签的peeled引用。分支/标签同名时要求显式 `refs/heads/` 或 `refs/tags/`，不会猜测。仅40位完整SHA-1作为已固定输入直接返回，短哈希不再冒充固定提交；直接返回不代表服务器已验证该提交存在，后续文件查询验证可访问性。暂不支持Git v2或SHA-256仓库。

在线 `DownloadSnapshotAsync` 在离线分支处理之后解析一次引用，文件列表和每个下载请求均固定到同一Commit；清单分别保存 RequestedRevision 与 ResolvedRevision。部分快照只能与同一ResolvedRevision合并。离线模式保持不联网。

真实BGE解析结果与 `git ls-remote` 独立查询一致：`master` → `160f4d645d32abe3cabc5af6b6b39823eadf3c0e`。随后.NET从master只下载743字节config.json，清单同时记录master与该Commit，文件SHA-256为 `094f8e891b932f2000c92cfc663bac4c62069f5d8af5b5278c4306aef3084750`。证据：`artifacts/acceptance/fr01-live-branch-snapshot/.modelscope-net-manifest.json`。

2026-09-06 后续入口统一：`GetModelAsync` 先解析一次，详情和文件列表使用同一Commit，返回原始Revision和固定Commit；独立 `DownloadFileAsync` 也先固定一次，所有重试沿用该Commit。续传临时路径从 `.incomplete` 改为 `.incomplete.<identity-sha256>`，identity为模型ID、Commit、文件路径，避免不同版本或不同模型共用残留字节。旧格式及其他版本临时文件保留但不再复用；已有完成文件路径不变。新增测试确认详情两请求一致、网络故障后不重新解析移动分支、不续传其他Commit或旧格式文件。全套163项通过（Hub31、Runtime118、ASP.NET9、Cli5），证据 `artifacts/acceptance/fr01-entrypoints/`。

## 引用缓存换代（2026-09-06）

默认缓存现在按 `snapshots/<owner>/<model>/<Commit>` 存放快照。命名引用在 `refs/<模型ID与引用名的SHA-256>.ref` 保存最后成功下载的40位Commit。分支更新进入新目录，不覆盖旧Commit目录；在线同名引用采用文件锁串行解析、下载与发布，只有快照清单写完后才用临时文件重命名更新指针。下载失败不发布新指针。

离线分支读取指针，随后检验目标快照的模型ID、版本和文件完整性，全程不联网；旧Commit也可直接离线访问。指针限长并验证完整Commit，损坏时拒绝而非当作目录路径。无指针时保留旧版本命名目录的离线兼容读取，但在线新下载不再覆盖该旧目录。

显式 `LocalDirectory` 已含不同模型或Commit清单时拒绝覆盖，要求调用方选择新目录；同Commit的补充下载仍受支持。引用指针不提供掉电/目录fsync事务保证，失败可能遗留未发布目录，不能宣称已有生产分布式缓存事务。持有旧快照路径的会话不会因分支目录换代而被改指向，但尚需跨平台长稳及多进程换代压力验证。

新增测试验证成功/失败/再次成功的两代切换、旧内容不变、离线分支指向最新成功代、离线固定Commit可访问旧代、损坏指针拒绝和显式目录防覆盖。Hub33项通过；完整回归证据 `artifacts/acceptance/fr01-cache-generations-full/`。

## 真实附注标签与四进程共享缓存（2026-09-06）

`tools/live_tag_cache_check.py` 完成一轮公开模型实测，证据 `artifacts/hub-livechecks/73e6e9ae679d48458cbc005c19aa40a8/report.json`。目标 `damo/nlp_structbert_sentiment-classification_chinese-base@v1.0.0` 的Git标签对象为 `0010e3a492a99ffca753f0887a8bc38852600fda`，剥离后的模型提交为 `c43720db271962bee1087d77088d18efe312ee78`；纯.NET解析返回后者，与独立 `git ls-remote` 一致。

四个独立.NET CLI进程同时请求同一标签/缓存，全部成功并返回同一Commit目录。仅下载554字节config.json，清单身份/大小/哈希再次检查通过，SHA-256为 `f6b74d131b62af8766c18dac2e703f0fb4da5ffab536df67075fc25f6c26cbc8`。随后将Endpoint设为不可连接的 `https://127.0.0.1:1`，标签名和固定Commit两种离线访问仍成功，均返回同一路径。

复验前构建Release CLI，再执行 `python3 tools/live_tag_cache_check.py`；脚本使用独立证据目录并保留每个子进程标准输出/错误、Git对照结果和清单。此处是四进程共享缓存冒烟，不是分支移动期间的压力测试、长稳或掉电事务验证；不下载权重，不做该模型推理认证，不将其计入P0代表模型或金标准数量。

## 错误状态与敏感信息（2026-09-06）

HTTP失败及HTTP200内业务Code失败统一分类：401/403为AuthenticationRequired、404为ModelNotFound、429为RemoteApiRateLimited、其他失败为RemoteApiUnavailable；429和5xx标记可重试。标记可重试不等于业务信封自动重发，实际重试策略仍由调用入口决定。引用公告没有所请求名称时独立返回RevisionNotFound，名称歧义返回InvalidRequest。没有可靠资源上下文的HTTP404不猜测为文件或版本不存在。

Success=false、非法Success/Code、Code失败但Success=true均拒绝。文件列表容器格式错误不再当作空列表；合法空数组仍可返回。Hub HTTP异常不读取或输出失败响应正文，也不输出私有模型ID；业务错误不回显Message，防止上游回显凭证、签名URL或请求内容进入日志。

新增10项测试覆盖上述映射、矛盾/非法信封和敏感正文不回显；全套175项通过（Hub43、Runtime118、ASP.NET9、Cli5），证据 `artifacts/acceptance/fr01-errors/`。此次仅覆盖Hub，其他运行时HTTP错误正文仍需单独核对，不能据此宣称所有日志出口已脱敏。

FR-01仍待最终关闭：私有/受限仓库Git认证、跨进程分支换代压力及剩余错误场景仍需验证。不能把本次配置文件下载证明扩大为所有认证模型或生产集群通过。
