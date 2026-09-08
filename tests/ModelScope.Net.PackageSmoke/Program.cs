using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelScope.Net.AspNetCore;
using ModelScope.Net.Hub;
using ModelScope.Net.Runtime;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddModelScopeNet(
    configureHub: options => options.Endpoint = new Uri("https://modelscope.cn"),
    configureRouter: options => options.AllowRemoteFallback = false,
    configureTelemetry: options => options.IncludeModelFingerprint = false);

using var host = builder.Build();
_ = host.Services.GetRequiredService<IModelScopeHubClient>();
_ = host.Services.GetRequiredService<RuntimeRouter>();
_ = host.Services.GetRequiredService<ModelScopeTelemetry>();
