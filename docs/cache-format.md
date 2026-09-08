# 下载缓存格式

## 目标

缓存必须支持下载中断恢复、内容去重、离线加载和完整性复核。缓存内容不是可信输入；每次离线打开快照时仍校验文件大小和仓库提供的 SHA-256。

## 目录

```text
<cache>/
  blobs/<sha-prefix>/<sha-or-derived-key>
  snapshots/<owner>/<model>/<revision>/
    .modelscope-net-manifest.json
    .modelscope-net.lock
    <repository files>
```

- Blob 优先使用仓库 SHA-256 作为键；缺少 SHA 时使用 Model ID、Revision 和文件路径的派生键。
- Snapshot 文件优先链接到绝对 Blob 路径；平台不支持链接时复制文件。
- `.incomplete` 文件是可续传的临时内容，只有大小和 SHA 校验通过后才原子替换正式 Blob。
- Snapshot 与 Blob 分别使用独占文件锁，防止多个进程同时写入同一制品。
- Manifest 通过唯一临时文件写入后原子替换，避免进程中断留下半个 JSON。
- 未过滤的快照下载用本次文件清单整体替换 Manifest。
- 带 allow/ignore 的过滤下载只更新本次下载的条目，并与同一 Model ID/Revision 的现有 Manifest 合并；已校验的未触及文件保留。损坏或缺失的旧条目在合并时丢弃，不会把完整缓存改写成子集。

## Revision 语义

`requestedRevision` 保存调用方请求的分支、Tag 或 Commit；`resolvedRevision` 只有在服务端明确解析时才写入解析值。当前技术预览对分支请求保留原值，因此需要可重复构建时必须传入固定 Commit，而不能传 `master`。

## 离线模式

`LocalFilesOnly=true` 不发起网络请求。客户端读取 Manifest，阻止越界路径，并逐项检查快照文件存在性、大小和 SHA。任一文件缺失或损坏都会返回 `DownloadIntegrityFailed`，不会把快照交给运行时。
