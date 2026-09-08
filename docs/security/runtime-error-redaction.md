# 运行时传输错误脱敏验收

2026-09-06：Remote、Python HTTP、GGUF的非成功HTTP响应不再读取并拼接原始正文，而是返回固定的运行时名称/HTTP状态信息，保持原ModelScopeErrorCode和重试标志。Python gRPC保持StatusCode与受支持的安全分类，但不输出Status.Detail，也不挂接原RpcException，避免通过InnerException/ToString再次暴露原始详情。

新增9项测试：三个HTTP运行时各覆盖普通和流式调用（6项）；gRPC使用真实客户端协议帧注入PermissionDenied，覆盖一般鉴权失败、allow_remote_code和model_path_outside_allowed_roots三个分支（3项）。所有测试检查敏感正文不出现在异常ToString中，错误分类不变，gRPC InnerException为空。

全套184项通过、0跳过：Hub43 + Runtime127 + ASP.NET9 + Cli5。证据目录 `artifacts/acceptance/runtime-transport-redaction/`。此前HTTP单独检查点181项位于 `artifacts/acceptance/runtime-http-redaction/`。

此结论仅覆盖被测的上游失败响应正文与gRPC状态详情，不宣称成功模型输出、所有第三方异常、HTTP200内业务错误、宿主自定义日志或遥测出口均已验证；最终安全核对仍须逐项检查。错误正文不再作为诊断信息供调用方展示，排障应结合状态码与服务端受控日志。
