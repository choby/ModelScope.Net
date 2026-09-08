using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Hub;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Gguf;
using ModelScope.Net.Runtime.Onnx;
using ModelScope.Net.Runtime.Python;
using ModelScope.Net.Runtime.Remote;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "info" => await InfoAsync(args[1..]),
                "revisions" => await RevisionsAsync(args[1..]),
                "resolve" => await ResolveAsync(args[1..]),
                "download" => await DownloadAsync(args[1..]),
                "inspect" => await InspectAsync(args[1..]),
                "run" => await RunModelAsync(args[1..]),
                "login" => Login(args[1..]),
                "logout" => Logout(args[1..]),
                "cache" => await CacheAsync(args[1..]),
                "progress" => Progress(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is ModelScopeException modelScope
                ? $"{modelScope.Code}: {modelScope.Message}"
                : exception.Message);
            return 1;
        }
    }

    private static async Task<int> InfoAsync(string[] args)
    {
        var modelId = RequireModelId(args);
        var revision = GetOption(args, "--revision");
        using var httpClient = new HttpClient();
        var client = CreateHubClient(httpClient, args);
        var model = await client.GetModelAsync(modelId, revision);
        Console.WriteLine(JsonSerializer.Serialize(model, JsonOptions));
        return 0;
    }

    private static async Task<int> RevisionsAsync(string[] args)
    {
        var modelId = RequireModelId(args);
        using var httpClient = new HttpClient();
        var revisions = await CreateHubClient(httpClient, args).GetModelRevisionsAsync(modelId);
        Console.WriteLine(JsonSerializer.Serialize(revisions, JsonOptions));
        return 0;
    }

    private static async Task<int> ResolveAsync(string[] args)
    {
        using var httpClient = new HttpClient();
        var commit = await CreateHubClient(httpClient, args).ResolveModelRevisionAsync(RequireModelId(args), GetOption(args, "--revision"));
        Console.WriteLine(commit);
        return 0;
    }

    private static async Task<int> DownloadAsync(string[] args)
    {
        var modelId = RequireModelId(args);
        var revision = GetOption(args, "--revision");
        var destination = GetOption(args, "--local-dir");
        var allowPatterns = GetListOption(args, "--allow");
        var ignorePatterns = GetListOption(args, "--ignore");
        var localFilesOnly = HasFlag(args, "--local-files-only");
        using var httpClient = new HttpClient();
        var client = CreateHubClient(httpClient, args);
        var progress = new Progress<DownloadProgress>(value =>
        {
            Console.Error.Write($"\r{value.CompletedFiles}/{value.TotalFiles} {value.Path} {value.BytesReceived}/{value.TotalBytes?.ToString() ?? "?"} bytes");
        });
        var snapshot = await client.DownloadSnapshotAsync(
            new SnapshotDownloadRequest(
                modelId,
                revision,
                destination,
                allowPatterns,
                ignorePatterns,
                LocalFilesOnly: localFilesOnly),
            progress);
        Console.Error.WriteLine();
        Console.WriteLine(snapshot.Directory);
        return 0;
    }

    private static async Task<int> RunModelAsync(string[] args)
    {
        var target = RequireTarget(args, "run requires a model directory or owner/name.");
        var runtimeName = GetOption(args, "--runtime");
        var localDirectory = ResolveRunDirectory(target, args);
        var remoteRequested = IsRemoteRuntime(runtimeName);
        if (localDirectory is not null && !remoteRequested)
        {
            return await RunLocalAsync(localDirectory, args, runtimeName);
        }

        return await RunRemoteAsync(target, localDirectory, args);
    }

    private static async Task<int> RunLocalAsync(string modelDirectory, string[] args, string? preferredRuntime)
    {
        var capabilities = await new ModelInspector().InspectAsync(modelDirectory);
        var task = GetOption(args, "--task") ?? capabilities.Task;
        if (!string.IsNullOrWhiteSpace(task) &&
            !string.Equals(task, capabilities.Task, StringComparison.OrdinalIgnoreCase))
        {
            capabilities = capabilities with { Task = task };
        }

        await using var resources = new RunResources();
        var router = CreateLocalRouter(args, resources, modelDirectory);
        await using var session = await router.CreateSessionAsync(
            capabilities,
            preferredRuntime,
            CancellationToken.None).ConfigureAwait(false);
        await InvokeSessionAsync(
            session,
            ResolveTask(task, capabilities, remote: false),
            args,
            useChatPayload: ShouldUseChatPayload(preferredRuntime, capabilities)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunRemoteAsync(string target, string? inspectedDirectory, string[] args)
    {
        string modelId;
        string revision;
        string? taskOverride = GetOption(args, "--task");
        if (inspectedDirectory is not null)
        {
            var inspected = await new ModelInspector().InspectAsync(inspectedDirectory);
            modelId = inspected.ModelId ?? throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "Remote run from a local directory requires a snapshot manifest with a Model ID.");
            revision = GetOption(args, "--revision") ?? inspected.Revision ?? "master";
            taskOverride ??= inspected.Task;
        }
        else
        {
            modelId = ModelId.Parse(target).ToString();
            revision = GetOption(args, "--revision") ?? "master";
        }

        var task = taskOverride ?? "chat";
        var endpoint = GetOption(args, "--remote-endpoint") ??
            Environment.GetEnvironmentVariable("MODELSCOPE_INFERENCE_ENDPOINT") ??
            "https://api-inference.modelscope.cn/";
        var token = ResolveToken(args);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.AuthenticationRequired,
                "API Inference requires MODELSCOPE_API_TOKEN or --token.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var runtime = new ModelScopeRemoteRuntime(httpClient, new RemoteRuntimeOptions
        {
            Endpoint = new Uri(endpoint),
            Token = token,
        });
        var capabilities = new ModelCapabilities(
            inspectedDirectory ?? string.Empty,
            modelId,
            revision,
            task,
            [],
            [],
            [],
            false,
            []);
        await using var session = await runtime.CreateSessionAsync(capabilities);
        await InvokeSessionAsync(session, task, args, useChatPayload: true);
        return 0;
    }

    private static async Task InvokeSessionAsync(
        IModelSession session,
        string task,
        string[] args,
        bool useChatPayload)
    {
        using var payloadDocument = CreatePayloadDocument(args, task, useChatPayload);
        var request = new ModelRequest(task, payloadDocument.RootElement.Clone(), HasFlag(args, "--stream"));
        if (request.Stream)
        {
            await foreach (var item in session.InvokeStreamingAsync(request))
            {
                if (!item.IsTerminal)
                {
                    Console.WriteLine(item.Data.GetRawText());
                }
            }

            return;
        }

        var response = await session.InvokeAsync(request);
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static RuntimeRouter CreateLocalRouter(
        string[] args,
        RunResources resources,
        string modelDirectory)
    {
        var allowRemoteCode = HasFlag(args, "--allow-remote-code");
        var onnx = new OnnxRuntimeAdapter();
        var runtimes = new List<IModelRuntime>
        {
            onnx,
            new OnnxTextGenerationRuntime(),
            new OnnxEmbeddingRuntime(onnx),
            new OnnxTextClassificationRuntime(onnx),
            new OnnxImageClassificationRuntime(onnx),
        };

        LocalLlamaServerSupervisor? llamaSupervisor = null;
        var llamaExecutable = GetOption(args, "--llama-server");
        var llamaEndpoint = GetOption(args, "--llama-server-endpoint");
        if (!string.IsNullOrWhiteSpace(llamaExecutable))
        {
            llamaSupervisor = new LocalLlamaServerSupervisor(new LlamaServerProcessOptions
            {
                Executable = llamaExecutable,
                Port = GetFreeTcpPort(),
            });
            resources.Add(llamaSupervisor);
        }

        runtimes.Add(new GgufRuntimeAdapter(
            httpClient: null,
            new GgufRuntimeOptions
            {
                LlamaServerEndpoint = string.IsNullOrWhiteSpace(llamaEndpoint) ? null : new Uri(llamaEndpoint),
                ApiKey = GetOption(args, "--llama-server-api-key"),
            },
            llamaSupervisor));

        runtimes.Add(CreatePythonRuntime(args, resources, modelDirectory, allowRemoteCode));

        return new RuntimeRouter(
            runtimes,
            new RuntimeRouterOptions
            {
                Mode = RuntimePolicyMode.Development,
                AllowRemoteCode = allowRemoteCode,
            });
    }

    private static IModelRuntime CreatePythonRuntime(
        string[] args,
        RunResources resources,
        string modelDirectory,
        bool allowRemoteCode)
    {
        var pythonOptions = new PythonWorkerOptions
        {
            AllowRemoteCode = allowRemoteCode,
            ApiKey = GetOption(args, "--python-api-key"),
        };
        var pythonEndpoint = GetOption(args, "--python-endpoint");
        LocalPythonWorkerSupervisor? supervisor = null;
        if (string.IsNullOrWhiteSpace(pythonEndpoint) && ShouldStartPythonWorker(args))
        {
            var script = ResolvePythonWorkerScript(args)
                ?? throw new ModelScopeException(
                    ModelScopeErrorCode.RuntimeNotInstalled,
                    "Python worker script was not found. Pass --python-script or --python-endpoint.");
            var port = GetFreeTcpPort();
            var backend = GetOption(args, "--python-backend") ?? "modelscope";
            supervisor = new LocalPythonWorkerSupervisor(new LocalPythonWorkerProcessOptions
            {
                PythonExecutable = ResolvePythonExecutable(args),
                WorkerScript = script,
                WorkingDirectory = Path.GetDirectoryName(script),
                Arguments =
                {
                    "--port", port.ToString(),
                    "--backend", backend,
                    "--workers", "1",
                    "--max-sessions", "2",
                },
                AllowedModelRoots = { modelDirectory },
            });
            resources.Add(supervisor);
            pythonEndpoint = $"http://127.0.0.1:{port}";
            pythonOptions.ApiKey = supervisor.ApiKey;
            pythonOptions.RecoveryAttempts = 10;
            pythonOptions.RecoveryDelay = TimeSpan.FromMilliseconds(250);
        }

        if (!string.IsNullOrWhiteSpace(pythonEndpoint))
        {
            pythonOptions.Endpoint = new Uri(pythonEndpoint);
        }

        var pythonTransport = GetOption(args, "--python-transport");
        var useGrpc = pythonOptions.Endpoint is not null &&
            !string.Equals(pythonTransport, "http", StringComparison.OrdinalIgnoreCase);
        if (useGrpc)
        {
            pythonOptions.Transport = PythonWorkerTransport.Grpc;
        }

        var pythonHttp = new HttpClient { Timeout = pythonOptions.RequestTimeout };
        resources.Add(pythonHttp);
        IModelRuntime pythonRuntime = useGrpc
            ? new GrpcPythonWorkerRuntime(pythonHttp, pythonOptions, supervisor)
            : new PythonWorkerRuntime(pythonHttp, pythonOptions, supervisor);
        if (pythonRuntime is IDisposable disposablePython)
        {
            resources.Add(disposablePython);
        }

        return pythonRuntime;
    }

    private static bool ShouldStartPythonWorker(string[] args) =>
        HasFlag(args, "--python-worker") ||
        !string.IsNullOrWhiteSpace(GetOption(args, "--python-executable")) ||
        string.Equals(GetOption(args, "--runtime"), "python", StringComparison.OrdinalIgnoreCase);

    private static string? ResolvePythonWorkerScript(string[] args)
    {
        var configured = GetOption(args, "--python-script");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var outputScript = Path.Combine(AppContext.BaseDirectory, "worker", "server.py");
        if (File.Exists(outputScript))
        {
            return outputScript;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var repoScript = Path.Combine(directory.FullName, "worker", "python", "server.py");
            if (File.Exists(repoScript))
            {
                return repoScript;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string ResolvePythonExecutable(string[] args)
    {
        var configured = GetOption(args, "--python-executable") ??
            Environment.GetEnvironmentVariable("MODELSCOPE_WORKER_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var local = Path.Combine(
                directory.FullName,
                ".worker-venv",
                OperatingSystem.IsWindows() ? Path.Combine("Scripts", "python.exe") : Path.Combine("bin", "python"));
            if (File.Exists(local))
            {
                return local;
            }

            directory = directory.Parent;
        }

        return "python3";
    }

    private static JsonDocument CreatePayloadDocument(string[] args, string task, bool useChatPayload)
    {
        var payloadText = GetOption(args, "--payload");
        var prompt = GetOption(args, "--prompt");
        if (string.IsNullOrWhiteSpace(payloadText) && string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("run requires --prompt TEXT or --payload JSON.");
        }

        if (!string.IsNullOrWhiteSpace(payloadText))
        {
            return JsonDocument.Parse(payloadText);
        }

        if (UsesChatPrompt(task, useChatPayload))
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                messages = new[] { new { role = "user", content = prompt } },
            }));
        }

        if (IsImageTask(task) && File.Exists(prompt))
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                image = Convert.ToBase64String(File.ReadAllBytes(prompt)),
            }));
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(new { text = prompt }));
    }

    private static string ResolveTask(string? task, ModelCapabilities capabilities, bool remote)
    {
        if (!string.IsNullOrWhiteSpace(task))
        {
            return task;
        }

        if (!string.IsNullOrWhiteSpace(capabilities.Task))
        {
            return capabilities.Task;
        }

        if (remote || capabilities.Artifacts.Any(artifact => artifact.Format == ModelArtifactFormat.Gguf))
        {
            return "chat";
        }

        return "unknown";
    }

    private static string? ResolveRunDirectory(string target, string[] args)
    {
        var localDir = GetOption(args, "--local-dir");
        if (!string.IsNullOrWhiteSpace(localDir))
        {
            var configured = Path.GetFullPath(localDir);
            return Directory.Exists(configured)
                ? configured
                : throw new DirectoryNotFoundException($"Model directory '{configured}' does not exist.");
        }

        var candidate = Path.GetFullPath(target);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static bool IsRemoteRuntime(string? runtimeName) =>
        string.Equals(runtimeName, "remote", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseChatPayload(string? preferredRuntime, ModelCapabilities capabilities) =>
        string.Equals(preferredRuntime, "gguf", StringComparison.OrdinalIgnoreCase) ||
        (string.IsNullOrWhiteSpace(preferredRuntime) &&
         capabilities.Artifacts.Any(artifact => artifact.Format == ModelArtifactFormat.Gguf) &&
         !capabilities.Artifacts.Any(artifact => artifact.Format == ModelArtifactFormat.Onnx));

    private static bool UsesChatPrompt(string task, bool useChatPayload) =>
        useChatPayload ||
        task.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
        task.Equals("chat-completion", StringComparison.OrdinalIgnoreCase);

    private static bool IsImageTask(string task) =>
        task.Equals("image-classification", StringComparison.OrdinalIgnoreCase) ||
        task.Equals("image-classification-imagenet", StringComparison.OrdinalIgnoreCase);

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int Login(string[] args)
    {
        var token = GetOption(args, "--token") ??
            Environment.GetEnvironmentVariable("MODELSCOPE_API_TOKEN") ??
            Environment.GetEnvironmentVariable("MODELSCOPE_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("login requires --token or MODELSCOPE_API_TOKEN.");
        }

        var store = CreateCredentialStore(args);
        store.SaveToken(token);
        Console.WriteLine($"Credentials saved to {store.CredentialPath}");
        return 0;
    }

    private static int Logout(string[] args)
    {
        var store = CreateCredentialStore(args);
        Console.WriteLine(store.DeleteToken()
            ? $"Credentials removed from {store.CredentialPath}"
            : $"No credentials found at {store.CredentialPath}");
        return 0;
    }

    private static async Task<int> CacheAsync(string[] args)
    {
        var command = args.Length > 0 && args[0].ToLowerInvariant() is "path" or "scan" or "verify"
            ? args[0]
            : "scan";
        var cacheDirectory = ResolveCacheDirectory(args);
        if (string.Equals(command, "path", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(cacheDirectory);
            return 0;
        }

        var inspector = new ModelScopeCacheInspector(cacheDirectory);
        if (string.Equals(command, "scan", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(inspector.Scan(), JsonOptions));
            return 0;
        }

        if (string.Equals(command, "verify", StringComparison.OrdinalIgnoreCase))
        {
            var result = await inspector.VerifyAsync();
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.IsValid ? 0 : 3;
        }

        throw new ArgumentException($"Unknown cache command '{command}'. Use path, scan, or verify.");
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("inspect requires a model directory.");
        }

        var capabilities = await new ModelInspector().InspectAsync(args[0]);
        Console.WriteLine(JsonSerializer.Serialize(capabilities, JsonOptions));
        return 0;
    }

    private static int Progress()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/migration-progress.md"));
        Console.WriteLine(path);
        return File.Exists(path) ? 0 : 2;
    }

    private static ModelScopeHubClient CreateHubClient(HttpClient httpClient, string[] args)
    {
        var endpoint = GetOption(args, "--endpoint") ??
            Environment.GetEnvironmentVariable("MODELSCOPE_ENDPOINT") ??
            "https://www.modelscope.cn";
        var token = ResolveToken(args);
        var cache = ResolveCacheDirectory(args);
        var options = new ModelScopeHubOptions
        {
            Endpoint = new Uri(endpoint),
            Token = token,
            CacheDirectory = cache,
        };

        return new ModelScopeHubClient(httpClient, options);
    }

    private static string? ResolveToken(string[] args) =>
        GetOption(args, "--token") ??
        Environment.GetEnvironmentVariable("MODELSCOPE_API_TOKEN") ??
        Environment.GetEnvironmentVariable("MODELSCOPE_ACCESS_TOKEN") ??
        CreateCredentialStore(args).LoadToken();

    private static ModelScopeCredentialStore CreateCredentialStore(string[] args)
    {
        var path = GetOption(args, "--credentials") ??
            Environment.GetEnvironmentVariable("MODELSCOPE_NET_CREDENTIALS_PATH");
        return new ModelScopeCredentialStore(path);
    }

    private static string ResolveCacheDirectory(string[] args) => Path.GetFullPath(
        GetOption(args, "--cache") ??
        Environment.GetEnvironmentVariable("MODELSCOPE_NET_CACHE") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "modelscope.net"));

    private static ModelId RequireModelId(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("A model ID in owner/name form is required.");
        }

        return ModelId.Parse(args[0]);
    }

    private static string RequireTarget(string[] args, string message)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(message);
        }

        return args[0];
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? GetListOption(string[] args, string name)
    {
        var value = GetOption(args, name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            ModelScope.Net CLI

              info owner/model [--revision REV] [--token TOKEN] [--endpoint URL]
              revisions owner/model [--token TOKEN] [--endpoint URL]
              resolve owner/model [--revision REV] [--token TOKEN] [--endpoint URL]
              download owner/model [--revision REV] [--local-dir PATH] [--cache PATH]
                                   [--allow GLOB[,GLOB]] [--ignore GLOB[,GLOB]] [--local-files-only]
              inspect MODEL_DIRECTORY
              run MODEL_DIRECTORY|--local-dir PATH [--task TASK] (--prompt TEXT | --payload JSON)
                  [--stream] [--runtime NAME] [--allow-remote-code]
                  [--python-worker] [--python-executable PATH] [--python-script PATH]
                  [--python-backend modelscope|echo] [--python-endpoint URL]
                  [--python-transport grpc|http] [--python-api-key KEY]
                  [--llama-server PATH] [--llama-server-endpoint URL] [--llama-server-api-key KEY]
              run owner/model [--runtime remote] [--task chat] (--prompt TEXT | --payload JSON)
                  [--stream] [--remote-endpoint URL] [--token TOKEN]
              login (--token TOKEN | MODELSCOPE_API_TOKEN) [--credentials PATH]
              logout [--credentials PATH]
              cache [path|scan|verify] [--cache PATH]
              progress

            Local run inspects a downloaded snapshot and routes to ONNX, GGUF, or Python.
            owner/name without a local directory uses ModelScope API Inference.
            --runtime selects a specific runtime such as onnx, onnx-embedding,
            onnx-text-generation, onnx-text-classification, onnx-image-classification,
            gguf, python, or remote.
            --python-worker or --runtime python starts a local gRPC worker for Python fallback.

            Token resolution order: --token, MODELSCOPE_API_TOKEN, MODELSCOPE_ACCESS_TOKEN,
            then the user-only credential file created by login.
            """);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private sealed class RunResources : IAsyncDisposable
    {
        private readonly List<IDisposable> _disposables = [];
        private readonly List<IAsyncDisposable> _asyncDisposables = [];

        public void Add(IDisposable disposable) => _disposables.Add(disposable);

        public void Add(IAsyncDisposable disposable) => _asyncDisposables.Add(disposable);

        public async ValueTask DisposeAsync()
        {
            for (var index = _asyncDisposables.Count - 1; index >= 0; index--)
            {
                await _asyncDisposables[index].DisposeAsync().ConfigureAwait(false);
            }

            for (var index = _disposables.Count - 1; index >= 0; index--)
            {
                _disposables[index].Dispose();
            }
        }
    }
}
