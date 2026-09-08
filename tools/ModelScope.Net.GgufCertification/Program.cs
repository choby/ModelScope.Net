using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelScope.Net.Runtime.Gguf;

var arguments = ParseArguments(args);
var modelPath = Path.GetFullPath(Require(arguments, "--model"));
var fileName = Require(arguments, "--file");
var reportPath = Path.GetFullPath(Require(arguments, "--report"));
var modelRoot = modelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var filePath = Path.GetFullPath(Path.Combine(modelRoot, fileName));
if (!filePath.StartsWith(modelRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    throw new ArgumentException("The GGUF file path escapes the model directory.");

using var manifestDocument = JsonDocument.Parse(
    await File.ReadAllTextAsync(Path.Combine(modelRoot, ".modelscope-net-manifest.json")));
var manifest = manifestDocument.RootElement;
var owner = manifest.GetProperty("modelId").GetProperty("owner").GetString();
var name = manifest.GetProperty("modelId").GetProperty("name").GetString();
var modelId = $"{owner}/{name}";
var requestedRevision = manifest.GetProperty("requestedRevision").GetString();
var resolvedRevision = manifest.GetProperty("resolvedRevision").GetString();
var expectedFile = manifest.GetProperty("files").EnumerateArray()
    .Single(item => string.Equals(item.GetProperty("path").GetString(), fileName, StringComparison.Ordinal));
var expectedSize = expectedFile.GetProperty("size").GetInt64();
var expectedSha256 = expectedFile.GetProperty("sha256").GetString();

var header = await GgufHeader.ReadAsync(filePath);
var policy = new GgufCompatibilityPolicy();
var policyResult = policy.Evaluate(header);
await using var fileStream = File.OpenRead(filePath);
var actualSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(fileStream));
var actualSize = fileStream.Length;
var artifactVerified = actualSize == expectedSize &&
    string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
var passed = artifactVerified && policyResult.IsAllowed;
var chatTemplateSha256 = header.ChatTemplate is null
    ? null
    : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(header.ChatTemplate)));

var report = new
{
    schemaVersion = 1,
    modelId,
    revision = resolvedRevision,
    requestedRevision,
    status = passed ? "passed" : "failed",
    generatedAt = DateTimeOffset.UtcNow,
    artifact = new
    {
        path = fileName,
        size = actualSize,
        sha256 = actualSha256,
        expectedSize,
        expectedSha256,
        verified = artifactVerified,
    },
    header = new
    {
        version = header.Version,
        tensorCount = header.TensorCount,
        metadataCount = header.MetadataCount,
        metadataEndOffset = header.MetadataEndOffset,
        architecture = header.Architecture,
        fileType = header.FileType,
        fileTypeName = header.FileTypeName,
        quantizationVersion = header.QuantizationVersion,
        tokenizerModel = header.TokenizerModel,
        hasChatTemplate = header.HasChatTemplate,
        chatTemplateLength = header.ChatTemplate?.Length,
        chatTemplateSha256,
    },
    policy = new
    {
        allowed = policyResult.IsAllowed,
        reason = policyResult.Reason,
        versions = policy.Versions.Order().ToArray(),
        architectures = policy.Architectures.Order(StringComparer.Ordinal).ToArray(),
        fileTypes = policy.FileTypes.Order().ToArray(),
        tokenizerModels = policy.TokenizerModels.Order(StringComparer.Ordinal).ToArray(),
        requireChatTemplate = policy.RequireChatTemplate,
        loadVerificationRequired = true,
    },
    environment = new
    {
        dotnet = Environment.Version.ToString(),
        os = Environment.OSVersion.ToString(),
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    },
};

Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
await File.WriteAllTextAsync(
    reportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine(
    $"gguf-certification-{(passed ? "passed" : "failed")} model={modelId} " +
    $"gguf={header.Version} architecture={header.Architecture} quantization={header.FileTypeName} " +
    $"tensors={header.TensorCount} metadata={header.MetadataCount} artifact_verified={artifactVerified}");
return passed ? 0 : 2;

static Dictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Arguments must be --name value pairs.");
        result[values[index]] = values[index + 1];
    }
    return result;
}

static string Require(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required argument '{name}' is missing.");
