# Compatibility Catalog

Catalog 只允许固定十六进制 Commit，不接受 `master`、`main` 或其他移动分支。`Certified` 条目必须来自已批准的发布清单和自动化兼容测试。

运行时使用 RSA-PSS/SHA-256 对原始 JSON 字节验签，签名文件保存 Base64 文本。任何空格、字段或制品 Hash 的修改都会使签名失效。

示例签名流程：

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out catalog-private.pem
openssl rsa -pubout -in catalog-private.pem -out catalog-public.pem
openssl dgst -sha256 -sigopt rsa_padding_mode:pss -sign catalog-private.pem \
  -out catalog-v1.sig.bin catalog-v1.example.json
openssl base64 -A -in catalog-v1.sig.bin -out catalog-v1.example.sig
```

私钥不进入代码仓库。CI 或发布系统只注入签名能力；应用仅分发公钥。
