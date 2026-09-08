using ModelScope.Net;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.Runtime.Tests;

public sealed class ModelInspectorTests
{
    [Theory]
    [InlineData("DistilBertForSequenceClassification", "text-classification")]
    [InlineData("MobileNetV2ForImageClassification", "image-classification")]
    [InlineData("Qwen2ForCausalLM", "text-generation")]
    [InlineData("BertModel", null)]
    [InlineData("T5ForConditionalGeneration", null)]
    public async Task TaskInferenceIsConservativeAndClearlyMarked(string architecture, string? expected)
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"),
                System.Text.Json.JsonSerializer.Serialize(new { architectures = new[] { architecture } }));
            var result = await new ModelInspector().InspectAsync(directory);
            Assert.Equal(expected, result.Task);
            Assert.Equal(expected is not null, result.Warnings.Any(w => w.StartsWith("Task was inferred", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("BertModel")]
    [InlineData("Qwen2ForCausalLM")]
    public async Task UnknownOrConflictingArchitecturesDoNotProduceTaskGuess(string other)
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"),
                System.Text.Json.JsonSerializer.Serialize(new { architectures = new[] { "DistilBertForSequenceClassification", other } }));
            Assert.Null((await new ModelInspector().InspectAsync(directory)).Task);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExplicitTaskIsNeverOverwrittenByArchitectureInference()
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"),
                "{\"task\":\"custom-task\",\"architectures\":[\"DistilBertForSequenceClassification\"]}");
            var result = await new ModelInspector().InspectAsync(directory);
            Assert.Equal("custom-task", result.Task);
            Assert.DoesNotContain(result.Warnings, w => w.StartsWith("Task was inferred", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task EntryLimitAllowsBoundaryAndRejectsPartialInspection()
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "model.onnx"), "fixture");
            var inspector = new ModelInspector(new(MaximumEntries: 1));
            Assert.Single((await inspector.InspectAsync(directory)).Artifacts);
            await File.WriteAllTextAsync(Path.Combine(directory, "extra.txt"), "fixture");
            var error = await Assert.ThrowsAsync<ModelScopeException>(() => inspector.InspectAsync(directory));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
            Assert.DoesNotContain(directory, error.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DepthLimitRejectsTraversalAndIgnoredRootDirectoriesArePruned()
    {
        var directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git", "deep", "nested"));
            var inspector = new ModelInspector(new(MaximumEntries: 2, MaximumDirectoryDepth: 0));
            Assert.Empty((await inspector.InspectAsync(directory)).Artifacts);
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            var error = await Assert.ThrowsAsync<ModelScopeException>(() => inspector.InspectAsync(directory));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DirectoryLinkLoopIsRejectedInsteadOfFollowed()
    {
        var directory = CreateTempDirectory();
        var link = Path.Combine(directory, "loop");
        try
        {
            Directory.CreateSymbolicLink(link, directory);
            var error = await Assert.ThrowsAsync<ModelScopeException>(() => new ModelInspector().InspectAsync(directory));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileLinksRetainContentSizeAndReadmeDiagnostics()
    {
        var directory = CreateTempDirectory();
        var blobs = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(blobs, "weights"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(blobs, "card"), "---\nlicense: MIT\n---");
            File.CreateSymbolicLink(Path.Combine(directory, "model.onnx"), Path.Combine(blobs, "weights"));
            File.CreateSymbolicLink(Path.Combine(directory, "README.md"), Path.Combine(blobs, "card"));
            var result = await new ModelInspector().InspectAsync(directory + Path.DirectorySeparatorChar);
            Assert.Equal(7, Assert.Single(result.Artifacts).Size);
            Assert.Contains(result.Diagnostics!.Licenses, item => item.Declaration == "MIT");
        }
        finally { Directory.Delete(directory, recursive: true); Directory.Delete(blobs, recursive: true); }
    }

    [Fact]
    public async Task CancelledInspectionDoesNotStartTraversal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelInspector().InspectAsync("missing-fixture-directory", cancellation.Token));
    }

    [Fact]
    public void InvalidInspectionLimitsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelInspector(new(MaximumEntries: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelInspector(new(MaximumDirectoryDepth: -1)));
    }

    [Theory]
    [InlineData("config.json", 1048576)]
    [InlineData(".modelscope-net-manifest.json", 16777216)]
    public async Task CoreMetadataAtExactLimitIsAccepted(string name, int maximum)
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, name), "{}" + new string(' ', maximum - 2));
            var result = await new ModelInspector().InspectAsync(directory);
            Assert.False(result.ContainsRemoteCode);
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("could not be parsed", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task NonObjectConfigurationRemainsUnknown(string json)
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), json);
            var result = await new ModelInspector().InspectAsync(directory);
            Assert.True(result.ContainsRemoteCode);
            Assert.Contains(result.Warnings, warning => warning.Contains("status is unknown", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("config.json", 1048576)]
    [InlineData("configuration.json", 1048576)]
    [InlineData("model_index.json", 1048576)]
    [InlineData(".modelscope-net-manifest.json", 16777216)]
    public async Task OversizedCoreMetadataFailsClosed(string name, int maximum)
    {
        var directory = CreateTempDirectory();
        try
        {
            using (var file = File.Create(Path.Combine(directory, name))) file.SetLength(maximum + 1L);
            var exception = await Assert.ThrowsAsync<ModelScopeException>(() => new ModelInspector().InspectAsync(directory));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, exception.Code);
            Assert.DoesNotContain(directory, exception.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MalformedConfigurationIsConservativelyFlaggedWithoutContentLeak()
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), "{\"fixture-secret\":invalid}");
            await File.WriteAllTextAsync(Path.Combine(directory, ".modelscope-net-manifest.json"), "{\"fixture-secret\":invalid}");
            var result = await new ModelInspector().InspectAsync(directory);
            Assert.True(result.ContainsRemoteCode);
            Assert.Null(result.ModelId);
            Assert.Null(result.Revision);
            Assert.DoesNotContain("fixture-secret", string.Join(' ', result.Warnings));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("---\nlicense: apache-2.0\n---\nlicense: not-metadata", "apache-2.0")]
    [InlineData("\uFEFF---\r\nlicense: 'MIT' # declaration\r\n...\r\nbody", "MIT")]
    [InlineData("---\nlicense: [MIT, \"Apache-2.0\"]\n---", "MIT,Apache-2.0")]
    [InlineData("---\nlicense:\n  - MIT\n  - Apache-2.0\ntags:\n  - fixture\n---", "MIT,Apache-2.0")]
    [InlineData("---\nlicense:\n- MIT\n- BSD-3-Clause\n---", "MIT,BSD-3-Clause")]
    [InlineData("---\n\"license\" : MIT\n---", "MIT")]
    public async Task ReadmeFrontMatterPreservesLicenseDeclarationsAndSources(string text, string expected)
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), text);
            var diagnostics = (await new ModelInspector().InspectAsync(directory)).Diagnostics!;
            Assert.Equal(expected.Split(','), diagnostics.Licenses.Select(e => e.Declaration));
            Assert.All(diagnostics.Licenses, e => { Assert.StartsWith("README.md:", e.Source); Assert.True(e.RequiresReview); });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("---\nlicense: MIT\nlicense: Apache-2.0\n---")]
    [InlineData("---\nlicense: MIT\n\"license\": Apache-2.0\n---")]
    [InlineData("---\nlicense: MIT\nlicense : Apache-2.0\n---")]
    [InlineData("---\nlicense: !!python/object/apply:os.system [fixture-secret]\n---")]
    [InlineData("---\nlicense: &fixture-secret MIT\n---")]
    [InlineData("---\nlicense: *fixture-secret\n---")]
    [InlineData("---\nlicense: https://user:fixture-secret@example.invalid/license\n---")]
    [InlineData("---\nlicense: |\n  fixture-secret\n---")]
    [InlineData("---\nlicense: MIT\n  fixture-secret\n---")]
    [InlineData("---\nlicense:\n  - MIT\n    - fixture-secret\n---")]
    [InlineData("---\nlicense: []\n---")]
    [InlineData("---\nlicense: MIT\nbody")]
    public async Task UnsupportedReadmeMetadataRemainsUnknownWithoutEchoingContent(string text)
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), text);
            var diagnostics = (await new ModelInspector().InspectAsync(directory)).Diagnostics!;
            Assert.Empty(diagnostics.Licenses);
            Assert.Contains(diagnostics.Notes, note => note.Contains("manual", StringComparison.Ordinal));
            Assert.DoesNotContain("fixture-secret", string.Join(' ', diagnostics.Notes));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReadmeBodyAndNestedLicenseAreNotTopLevelLicenseMetadata()
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), "---\nmodel:\n  license: MIT\n---\nlicense: Apache-2.0");
            Assert.Empty((await new ModelInspector().InspectAsync(directory)).Diagnostics!.Licenses);
            await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), "---\nlicense: MIT\n---\n" + new string('x', 256 * 1024));
            var diagnostics = (await new ModelInspector().InspectAsync(directory)).Diagnostics!;
            Assert.Empty(diagnostics.Licenses);
            Assert.Contains(diagnostics.Notes, note => note.Contains("exceeds diagnostic metadata limit", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DiagnosticsReportDeclarationsWithoutExecutingDependencyDirectives()
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), """{"license":"Apache-2.0"}""");
            await File.WriteAllTextAsync(Path.Combine(directory, "LICENSE"), "fixture license evidence");
            await File.WriteAllBytesAsync(Path.Combine(directory, "model.onnx"), [1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(directory, "requirements.txt"),
                "torch==2.5.1\ntransformers>=4.40,<5\n# comment\n-r /private/secret\npackage @ https://user:fixture-secret@example.invalid/repo\n");
            var result = await new ModelInspector().InspectAsync(directory);
            var diagnostics = Assert.IsType<ModelInspectionDiagnostics>(result.Diagnostics);
            Assert.Contains(diagnostics.Licenses, item => item.Declaration == "Apache-2.0" && item.RequiresReview);
            Assert.Contains(diagnostics.Licenses, item => item.Source == "LICENSE" && item.Declaration is null);
            Assert.Equal(2, diagnostics.DeclaredDependencies.Count);
            Assert.Equal("==2.5.1", diagnostics.DeclaredDependencies[0].VersionConstraint);
            Assert.Equal(3, diagnostics.ArtifactBytes);
            Assert.Null(diagnostics.RequiredRamBytes);
            Assert.Null(diagnostics.RequiredVramBytes);
            Assert.Contains(diagnostics.Notes, note => note.Contains("unsupported declaration", StringComparison.Ordinal));
            Assert.DoesNotContain("fixture-secret", string.Join(" ", diagnostics.Notes));
            Assert.True(result.ContainsRemoteCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MissingLicenseAndOversizedRequirementsRemainExplicitlyUnknown()
    {
        var directory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "requirements.txt"), new string('x', 256 * 1024 + 1));
            var diagnostics = (await new ModelInspector().InspectAsync(directory)).Diagnostics!;
            Assert.Empty(diagnostics.Licenses);
            Assert.Empty(diagnostics.DeclaredDependencies);
            Assert.Contains(diagnostics.Notes, note => note.Contains("license is unknown", StringComparison.Ordinal));
            Assert.Contains(diagnostics.Notes, note => note.Contains("exceeds diagnostic metadata limit", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InspectAsync_DetectsArtifactsAndConfigMetadata()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(directory, "model.gguf"), [0x47, 0x47, 0x55, 0x46]);
            File.WriteAllText(Path.Combine(directory, "model.safetensors"), "placeholder");
            File.WriteAllText(Path.Combine(directory, "configuration.py"), "# remote code");
            File.WriteAllText(Path.Combine(directory, "config.json"), """
                { "task": "text-generation", "architectures": ["DemoForCausalLM"] }
                """);
            File.WriteAllText(Path.Combine(directory, ".modelscope-net-manifest.json"), """
                {
                  "modelId": { "owner": "owner", "name": "model" },
                  "requestedRevision": "master",
                  "resolvedRevision": "abc123"
                }
                """);

            var capabilities = await new ModelInspector().InspectAsync(directory);

            Assert.Contains(capabilities.Artifacts, artifact => artifact.Format == ModelArtifactFormat.Onnx);
            Assert.Contains(capabilities.Artifacts, artifact => artifact.Format == ModelArtifactFormat.Gguf);
            Assert.Contains(capabilities.Artifacts, artifact => artifact.Format == ModelArtifactFormat.SafeTensors);
            Assert.True(capabilities.ContainsRemoteCode);
            Assert.Equal("text-generation", capabilities.Task);
            Assert.Equal("owner/model", capabilities.ModelId);
            Assert.Equal("abc123", capabilities.Revision);
            Assert.Contains("DemoForCausalLM", capabilities.Architectures);
            Assert.Equal("onnx", capabilities.RuntimeCandidates[0].RuntimeName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectAsync_ReportsTargetSizeForSymbolicLinkArtifacts()
    {
        var directory = CreateTempDirectory();
        try
        {
            var target = Path.Combine(directory, "model-target.onnx");
            var link = Path.Combine(directory, "model.onnx");
            File.WriteAllBytes(target, [1, 2, 3, 4, 5]);
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var capabilities = await new ModelInspector().InspectAsync(directory);

            var artifact = Assert.Single(capabilities.Artifacts, item => item.Path == "model.onnx");
            Assert.Equal(5, artifact.Size);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
