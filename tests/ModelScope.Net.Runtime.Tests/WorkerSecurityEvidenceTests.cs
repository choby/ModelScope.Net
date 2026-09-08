using System.Text.Json;

namespace ModelScope.Net.Runtime.Tests;

public sealed class WorkerSecurityEvidenceTests
{
    [Fact]
    public void WorkerSecurityMatrix_PassesApplicationAndDeploymentControls()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deployment", "worker-security-matrix.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("managed-worker-production-baseline", root.GetProperty("scope").GetString());
        Assert.Equal("application-and-deployment-controls-passed", root.GetProperty("status").GetString());
        var controls = root.GetProperty("controls").EnumerateArray().ToArray();
        Assert.True(controls.Length >= 15);
        Assert.All(controls, control => Assert.Equal("passed", control.GetProperty("status").GetString()));
        Assert.Contains(controls, control => control.GetProperty("id").GetString() == "AUTH-01");
        Assert.Contains(controls, control => control.GetProperty("id").GetString() == "NET-02");
    }

    [Fact]
    public void P405DeploymentProfile_ClosesFiveControlsAcrossDeclaredPlatforms()
    {
        var solution = FindSolutionDirectory();
        using var document = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(solution, "deployment", "p4-05-deployment-profile.json")));
        var root = document.RootElement;
        var controls = root.GetProperty("controls").EnumerateArray().ToArray();
        var platforms = root.GetProperty("platforms").EnumerateArray().ToArray();

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(5, controls.Length);
        Assert.All(controls, control => Assert.Equal("passed", control.GetProperty("status").GetString()));
        Assert.Equal(
            ["FS-01", "MTLS-01", "NET-02", "OS-01", "RESOURCE-01"],
            controls.Select(control => control.GetProperty("id").GetString()!).Order().ToArray());
        Assert.Contains(platforms, platform => platform.GetProperty("id").GetString() == "ubuntu-24.04-x64-cpu");
        Assert.Contains(platforms, platform => platform.GetProperty("id").GetString() == "linux-x64-nvidia-gpu");
        Assert.Contains(platforms, platform => platform.GetProperty("id").GetString() == "windows-server-2025-x64-cpu");
    }

    [Fact]
    public void KubernetesWorkerManifest_EnforcesSandboxNetworkAndResourceBoundaries()
    {
        var solution = FindSolutionDirectory();
        var deployment = File.ReadAllText(Path.Combine(solution, "deployment", "kubernetes", "base", "deployment.yaml"));
        var network = File.ReadAllText(Path.Combine(solution, "deployment", "kubernetes", "base", "network-policy.yaml"));
        var gpu = File.ReadAllText(Path.Combine(
            solution,
            "deployment",
            "kubernetes",
            "overlays",
            "linux-gpu",
            "workload-patch.yaml"));
        var kubelet = File.ReadAllText(Path.Combine(
            solution,
            "deployment",
            "kubernetes",
            "node-policy",
            "linux-worker-kubelet.yaml"));
        var dockerfile = File.ReadAllText(Path.Combine(
            solution,
            "deployment",
            "containers",
            "python-worker",
            "Dockerfile"));

        Assert.Contains("readOnlyRootFilesystem: true", deployment, StringComparison.Ordinal);
        Assert.Contains("runAsNonRoot: true", deployment, StringComparison.Ordinal);
        Assert.Contains("allowPrivilegeEscalation: false", deployment, StringComparison.Ordinal);
        Assert.Contains("type: RuntimeDefault", deployment, StringComparison.Ordinal);
        Assert.Contains("- ALL", deployment, StringComparison.Ordinal);
        Assert.Contains("readOnly: true", deployment, StringComparison.Ordinal);
        Assert.Contains("sizeLimit: 1Gi", deployment, StringComparison.Ordinal);
        Assert.Contains("memory: \"3Gi\"", deployment, StringComparison.Ordinal);
        Assert.Contains("registry.invalid/", deployment, StringComparison.Ordinal);
        Assert.Contains("@sha256:", deployment, StringComparison.Ordinal);
        Assert.Contains("ingress: []", network, StringComparison.Ordinal);
        Assert.Contains("egress: []", network, StringComparison.Ordinal);
        Assert.Contains("modelscope-net-gateway", network, StringComparison.Ordinal);
        Assert.Contains("nvidia.com/gpu: \"1\"", gpu, StringComparison.Ordinal);
        Assert.Contains("podPidsLimit: 256", kubelet, StringComparison.Ordinal);
        Assert.Contains(
            "ARG PYTHON_BASE_IMAGE=registry.invalid/",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("USER ${WORKER_UID}:${WORKER_GID}", dockerfile, StringComparison.Ordinal);
        Assert.Contains("python -m pip uninstall --yes pip", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsCpuPolicy_DeniesLocalPythonAndUntrustedModelCode()
    {
        var path = Path.Combine(
            FindSolutionDirectory(),
            "deployment",
            "windows-cpu",
            "execution-policy.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var denied = document.RootElement.GetProperty("deniedLocalWorkloads")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Contains("python-worker", denied);
        Assert.Contains("isolated-untrusted-model-code", denied);
        Assert.True(document.RootElement.GetProperty("requirements").GetProperty("remotePythonMutualTlsRequired").GetBoolean());
    }

    [Fact]
    public void ContainerBuildEvidence_PassesPinnedNonRootRestrictedRuntimeAndVulnerabilityGates()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deployment", "container-build-evidence.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var baseImage = root.GetProperty("baseImage");
        var workerImage = root.GetProperty("workerImage");
        var scan = root.GetProperty("vulnerabilityScan");
        var runtime = root.GetProperty("restrictedRuntimeSmoke");

        Assert.Equal("passed-local-linux-arm64-protocol-base", root.GetProperty("status").GetString());
        Assert.Matches("^sha256:[0-9a-f]{64}$", baseImage.GetProperty("digest").GetString());
        Assert.Matches("^sha256:[0-9a-f]{64}$", workerImage.GetProperty("imageId").GetString());
        Assert.Equal("65532:65532", workerImage.GetProperty("user").GetString());
        Assert.False(workerImage.GetProperty("dependencies").GetProperty("pipPresentAtRuntime").GetBoolean());
        Assert.True(scan.GetProperty("passed").GetBoolean());
        Assert.Equal(0, scan.GetProperty("critical").GetInt32());
        Assert.Equal(0, scan.GetProperty("high").GetInt32());
        Assert.Equal(0, scan.GetProperty("medium").GetInt32());
        Assert.Equal(0, scan.GetProperty("low").GetInt32());
        Assert.True(runtime.GetProperty("passed").GetBoolean());
        Assert.True(runtime.GetProperty("readOnlyRootFilesystem").GetBoolean());
        Assert.True(runtime.GetProperty("readOnlyModels").GetBoolean());
        Assert.Equal("none", runtime.GetProperty("networkMode").GetString());
        Assert.Equal(65532, runtime.GetProperty("runtimeUid").GetInt32());
    }

    private static string FindSolutionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ModelScope.Net.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the ModelScope.Net solution directory.");
    }
}
