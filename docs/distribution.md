# NuGet 技术预览分发

## 发布范围

当前源代码可生成 8 个版本严格对齐的 `0.1.0-preview.1` NuGet 包及对应符号包：

- `ModelScope.Net.Abstractions`
- `ModelScope.Net.Hub`
- `ModelScope.Net.Runtime`
- `ModelScope.Net.Runtime.Onnx`
- `ModelScope.Net.Runtime.Gguf`
- `ModelScope.Net.Runtime.Python`
- `ModelScope.Net.Runtime.Remote`
- `ModelScope.Net.AspNetCore`

CLI、示例、测试和认证工具不会被解决方案打包。该版本是本地可验证的技术预览制品，不是已发布到 nuget.org 的 1.0 包，也不表示发布清单、许可证或生产平台已经获批。

## 本地生成与验证

```bash
dotnet pack ModelScope.Net.sln -c Release --no-restore -o artifacts/packages
python3 tools/verify_nuget_packages.py artifacts/packages 0.1.0-preview.1
dotnet restore tests/ModelScope.Net.PackageSmoke/ModelScope.Net.PackageSmoke.csproj
dotnet build tests/ModelScope.Net.PackageSmoke/ModelScope.Net.PackageSmoke.csproj -c Release --no-restore
```

验证器要求包集合恰好完整、每包带 README/许可证/描述/仓库信息/net10.0 程序集、符号独立进入 `.snupkg`，并且所有内部依赖都使用同一精确预览版本。PackageSmoke 仅从本地 `artifacts/packages` 引用顶层 ASP.NET Core 包，编译公共 DI、Hub、Router 和 Telemetry 入口，从而检查传递依赖闭包。

## 发布前仍需完成

1. 确认最终包所有者、仓库地址、签名证书、NuGet 组织与保留策略。
2. 将预览版本提升到经批准的版本，并生成 release notes、SBOM、签名和 provenance。
3. 在干净的 Windows/Linux runner 从实际候选源恢复并编译 PackageSmoke。
4. 对 nupkg 和依赖执行最终漏洞/许可证扫描；当前本机验证不替代发布时扫描。
5. 完成 release-manifest、P4-07 及 Go/No-Go 后才能推送公开源。

包版本必须作为一个集合升级；不支持混用不同 ModelScope.Net 版本。外部原生依赖和 Python/llama.cpp 分发另受平台矩阵与固定摘要约束。
