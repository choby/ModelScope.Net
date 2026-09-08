# 凭据存储

CLI 的 Token 解析顺序为：`--token`、`MODELSCOPE_API_TOKEN`、`MODELSCOPE_ACCESS_TOKEN`、本地凭据文件。

```bash
modelscope-net login --token <token>
modelscope-net logout
```

默认凭据路径为 `~/.modelscope-net/credentials`，可通过 `--credentials` 或 `MODELSCOPE_NET_CREDENTIALS_PATH` 覆盖。写入使用同目录唯一临时文件和原子替换；Unix 文件权限为 `0600`、目录权限为 `0700`。CLI 成功信息、异常和缓存报告不打印 Token。

技术预览采用用户私有文件以兼容无桌面的 Windows/Linux 服务器。生产部署应优先由 Secret Manager、Kubernetes Secret 或进程环境注入，不应把 Token 烘焙进镜像、配置仓库或命令历史。
