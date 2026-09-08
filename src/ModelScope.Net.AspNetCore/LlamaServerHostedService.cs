using Microsoft.Extensions.Hosting;
using ModelScope.Net.Runtime.Gguf;

namespace ModelScope.Net.AspNetCore;

public sealed class LlamaServerHostedService(ILlamaServerSupervisor supervisor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => supervisor.StopAsync(cancellationToken);
}
