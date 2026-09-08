using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelScope.Net.Hub;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Gguf;
using ModelScope.Net.Runtime.Onnx;
using ModelScope.Net.Runtime.Python;
using ModelScope.Net.Runtime.Remote;

namespace ModelScope.Net.AspNetCore;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddModelScopeNet(
        this IServiceCollection services,
        Action<ModelScopeHubOptions>? configureHub = null,
        Action<RuntimeRouterOptions>? configureRouter = null,
        Action<RemoteRuntimeOptions>? configureRemote = null,
        Action<PythonWorkerOptions>? configurePython = null,
        Func<IServiceProvider, IPythonWorkerSupervisor?>? createPythonSupervisor = null,
        Action<OnnxRuntimeOptions>? configureOnnx = null,
        Action<OnnxTextGenerationOptions>? configureOnnxTextGeneration = null,
        Action<OnnxEmbeddingOptions>? configureOnnxEmbedding = null,
        Action<OnnxTextClassificationOptions>? configureOnnxTextClassification = null,
        Action<OnnxImageClassificationOptions>? configureOnnxImageClassification = null,
        Action<OnnxObjectDetectionOptions>? configureOnnxObjectDetection = null,
        Action<GgufRuntimeOptions>? configureGguf = null,
        Func<IServiceProvider, ILlamaServerSupervisor?>? createLlamaSupervisor = null,
        Action<ModelScopeTelemetryOptions>? configureTelemetry = null,
        Action<ModelSessionPoolOptions>? configureResources = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ModelScopeHubOptions>();
        services.AddOptions<RuntimeRouterOptions>();
        services.AddOptions<RemoteRuntimeOptions>();
        services.AddOptions<PythonWorkerOptions>();
        services.AddOptions<OnnxRuntimeOptions>();
        services.AddOptions<OnnxTextGenerationOptions>();
        services.AddOptions<OnnxEmbeddingOptions>();
        services.AddOptions<OnnxTextClassificationOptions>();
        services.AddOptions<OnnxImageClassificationOptions>();
        services.AddOptions<OnnxObjectDetectionOptions>();
        services.AddOptions<GgufRuntimeOptions>();
        services.AddOptions<ModelScopeTelemetryOptions>();
        services.AddOptions<ModelSessionPoolOptions>();
        if (configureHub is not null)
        {
            services.Configure(configureHub);
        }

        if (configureRouter is not null)
        {
            services.Configure(configureRouter);
        }

        if (configureRemote is not null)
        {
            services.Configure(configureRemote);
        }

        if (configurePython is not null)
        {
            services.Configure(configurePython);
        }

        if (configureOnnx is not null)
        {
            services.Configure(configureOnnx);
        }

        if (configureOnnxTextGeneration is not null)
        {
            services.Configure(configureOnnxTextGeneration);
        }

        if (configureOnnxEmbedding is not null)
        {
            services.Configure(configureOnnxEmbedding);
        }

        if (configureOnnxTextClassification is not null)
        {
            services.Configure(configureOnnxTextClassification);
        }

        if (configureOnnxImageClassification is not null)
        {
            services.Configure(configureOnnxImageClassification);
        }

        if (configureOnnxObjectDetection is not null)
        {
            services.Configure(configureOnnxObjectDetection);
        }

        if (configureGguf is not null)
        {
            services.Configure(configureGguf);
        }

        if (configureTelemetry is not null)
        {
            services.Configure(configureTelemetry);
        }

        if (configureResources is not null)
        {
            services.Configure(configureResources);
        }

        services.TryAddSingleton<IModelScopeAuditSink, LoggingModelScopeAuditSink>();
        services.AddSingleton(provider => new ModelScopeTelemetry(
            provider.GetRequiredService<IModelScopeAuditSink>(),
            provider.GetRequiredService<IOptions<ModelScopeTelemetryOptions>>().Value));
        services.AddSingleton<IModelSessionInstrumentation>(provider =>
            provider.GetRequiredService<ModelScopeTelemetry>());

        services.AddHttpClient("ModelScope.Net.Hub", (provider, client) =>
        {
            client.Timeout = provider.GetRequiredService<IOptions<ModelScopeHubOptions>>().Value.RequestTimeout;
        });
        services.AddTransient<IModelScopeHubClient>(provider => new ModelScopeHubClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("ModelScope.Net.Hub"),
            provider.GetRequiredService<IOptions<ModelScopeHubOptions>>().Value));

        services.AddHttpClient("ModelScope.Net.Remote", (provider, client) =>
        {
            client.Timeout = provider.GetRequiredService<IOptions<RemoteRuntimeOptions>>().Value.Timeout;
        });
        services.AddSingleton(provider => new ModelScopeRemoteRuntime(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("ModelScope.Net.Remote"),
            provider.GetRequiredService<IOptions<RemoteRuntimeOptions>>().Value));
        services.AddSingleton<ModelInspector>();
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<ModelScopeRemoteRuntime>());

        if (createPythonSupervisor is not null)
        {
            services.AddSingleton<IPythonWorkerSupervisor>(provider =>
                createPythonSupervisor(provider) ?? throw new InvalidOperationException("Python supervisor factory returned null."));
            services.AddHostedService<PythonWorkerHostedService>();
        }

        services.AddHttpClient("ModelScope.Net.Python", (provider, client) =>
        {
            client.Timeout = provider.GetRequiredService<IOptions<PythonWorkerOptions>>().Value.RequestTimeout;
        });
        services.AddSingleton(provider => new PythonWorkerRuntime(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("ModelScope.Net.Python"),
            provider.GetRequiredService<IOptions<PythonWorkerOptions>>().Value,
            provider.GetService<IPythonWorkerSupervisor>()));
        services.AddSingleton(provider => new GrpcPythonWorkerRuntime(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("ModelScope.Net.Python"),
            provider.GetRequiredService<IOptions<PythonWorkerOptions>>().Value,
            provider.GetService<IPythonWorkerSupervisor>()));
        services.AddSingleton<IPythonWorkerRuntime>(provider =>
            provider.GetRequiredService<IOptions<PythonWorkerOptions>>().Value.Transport == PythonWorkerTransport.Grpc
                ? provider.GetRequiredService<GrpcPythonWorkerRuntime>()
                : provider.GetRequiredService<PythonWorkerRuntime>());
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<IPythonWorkerRuntime>());
        services.AddSingleton(provider => new OnnxRuntimeAdapter(
            provider.GetRequiredService<IOptions<OnnxRuntimeOptions>>().Value));
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<OnnxRuntimeAdapter>());
        services.AddSingleton(provider => new OnnxTextGenerationRuntime(
            provider.GetRequiredService<IOptions<OnnxTextGenerationOptions>>().Value));
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<OnnxTextGenerationRuntime>());
        services.AddSingleton(provider => new OnnxEmbeddingRuntime(
            provider.GetRequiredService<OnnxRuntimeAdapter>(),
            provider.GetRequiredService<IOptions<OnnxEmbeddingOptions>>().Value));
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<OnnxEmbeddingRuntime>());
        services.AddSingleton(provider => new OnnxTextClassificationRuntime(
            provider.GetRequiredService<OnnxRuntimeAdapter>(),
            provider.GetRequiredService<IOptions<OnnxTextClassificationOptions>>().Value));
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<OnnxTextClassificationRuntime>());
        services.AddSingleton(provider => new OnnxImageClassificationRuntime(
            provider.GetRequiredService<OnnxRuntimeAdapter>(),
            provider.GetRequiredService<IOptions<OnnxImageClassificationOptions>>().Value));
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<OnnxImageClassificationRuntime>());
        services.AddSingleton(provider => new OnnxObjectDetectionRuntime(
            provider.GetRequiredService<OnnxRuntimeAdapter>(),
            provider.GetRequiredService<IOptions<OnnxObjectDetectionOptions>>().Value));
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<OnnxObjectDetectionRuntime>());
        if (createLlamaSupervisor is not null)
        {
            services.AddSingleton<ILlamaServerSupervisor>(provider =>
                createLlamaSupervisor(provider) ?? throw new InvalidOperationException("llama-server supervisor factory returned null."));
            services.AddHostedService<LlamaServerHostedService>();
        }
        services.AddHttpClient("ModelScope.Net.Gguf", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton(provider => new GgufRuntimeAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("ModelScope.Net.Gguf"),
            provider.GetRequiredService<IOptions<GgufRuntimeOptions>>().Value,
            provider.GetService<ILlamaServerSupervisor>()));
        services.AddSingleton<IModelRuntime>(provider => provider.GetRequiredService<GgufRuntimeAdapter>());
        services.AddSingleton(provider => new RuntimeRouter(
            provider.GetServices<IModelRuntime>(),
            provider.GetRequiredService<IOptions<RuntimeRouterOptions>>().Value,
            provider.GetRequiredService<IModelSessionInstrumentation>()));
        services.AddSingleton(provider => new ModelSessionPool(
            provider.GetRequiredService<RuntimeRouter>(),
            provider.GetRequiredService<IOptions<ModelSessionPoolOptions>>().Value));
        services.AddHealthChecks()
            .AddCheck<ModelScopeNetHealthCheck>("modelscope-net", tags: ["ready"])
            .AddCheck<PythonWorkerHealthCheck>("modelscope-python-worker", tags: ["ready", "worker"])
            .AddCheck<ModelSessionPoolHealthCheck>("modelscope-resource-pool", tags: ["ready", "runtime", "capacity"]);
        return services;
    }
}
