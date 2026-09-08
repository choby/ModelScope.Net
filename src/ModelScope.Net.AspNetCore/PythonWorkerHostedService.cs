using Microsoft.Extensions.Hosting;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.AspNetCore;

public sealed class PythonWorkerHostedService(IPythonWorkerSupervisor supervisor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        supervisor.EnsureRunningAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        supervisor.StopAsync(cancellationToken);
}
