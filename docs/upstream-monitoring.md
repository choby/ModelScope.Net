# P5-02 上游变化与依赖漂移检测

执行：python3 tools/upstream_monitor.py。离线只查依赖：加 --offline。

检测器针对认证登记清单中的每个唯一模型/源模型，对比固定 Commit 与 master 的完整文件列表，记录新增、删除、Revision/大小/SHA变化。固定版本清单还须匹配已登记文件摘要和大小；发现不符或无法查询时失败。该检查验证服务器元数据，实际下载文件完整性仍由认证流水线负责。

版本接口 /revisions 只提供分支名称和时间，不提供完整Commit；因此不把分支名称冒充Commit，也不依据时间推断身份。检测使用原SDK已有的 /repo/files 接口。文件清单的Revision字段变化也会触发重新认证，即使SHA未变，以保守保留元数据变化信号。

本地依赖基线：tests/compatibility/upstream-baseline.json，固定.NET SDK、项目包引用、Worker协议及Python金标准依赖配置文件摘要。配置改动/删除触发依赖漂移；它不查询NuGet/PyPI的最新可用版本。基线必须在审核及复认证后人工更新，不自动接受变化。

输出：artifacts/upstream-checks/<UUID>/report.json。状态为 unchanged、changed、failed、offline-only。退出码0表示本次检查完成且未发现漂移（离线结果仅表示依赖检查），3表示发现变化，2表示检查失败；离线报告 upstreamChecked=false，不得当作上游已检查。

变化报告 requiresRecertification=true，供P5-01重新认证入口使用；检测器不会移动已批准Revision、修改Catalog、自动接受新依赖或发出认证批准。P5-03仍需实现签名撤销和部署换代控制面。

CI：.github/workflows/upstream-monitor.yml 配置每日UTC02:30和手动入口，使用Ubuntu24.04、只读仓库权限，始终上传报告。变化及查询失败均非零退出，让CI明确需要处理。当前仅有本机在线执行证据，远程仓库尚未配置/执行，不能声称每日监控已运行。

TLS必须校验可信CA。本机Python默认CA缺失时可明确设置 SSL_CERT_FILE=/etc/ssl/cert.pem 或组织提供的可信CA路径，不得关闭证书校验。不使用ModelScope Token；私有仓库监控须另行接入授权凭据并复核报告脱敏，本轮公开模型范围不代表私库支持。
