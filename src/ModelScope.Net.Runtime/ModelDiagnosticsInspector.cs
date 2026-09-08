using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelScope.Net.Runtime;

internal static partial class ModelDiagnosticsInspector
{
    public static async Task<ModelInspectionDiagnostics> InspectAsync(string root,
        IReadOnlyList<ModelArtifact> artifacts, IReadOnlyList<string> rootFileNames, CancellationToken cancellationToken)
    {
        var licenses = new List<ModelLicenseEvidence>();
        var dependencies = new List<ModelDependencyEvidence>();
        var notes = new List<string>();
        foreach (var name in new[] { "README.md", "readme.md", "README.MD" }.Distinct(StringComparer.Ordinal))
        {
            // Case-insensitive filesystems can resolve several spellings to the same file.
            if (!rootFileNames.Contains(name, StringComparer.Ordinal)) continue;
            var readme = await ReadMetadataAsync(root, name, notes, cancellationToken).ConfigureAwait(false);
            if (readme is not null) ReadReadmeLicenses(readme, name, licenses, notes);
        }
        foreach (var name in new[] { "config.json", "configuration.json", "model_index.json" })
        {
            var text = await ReadMetadataAsync(root, name, notes, cancellationToken).ConfigureAwait(false);
            if (text is null) continue;
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Name.Equals("license", StringComparison.OrdinalIgnoreCase))
                    {
                        var declaration = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                        if (declaration is not null && LicenseIdentifier().IsMatch(declaration))
                            licenses.Add(new(name, declaration));
                        else notes.Add($"{name}: license declaration requires manual inspection.");
                    }
            }
            catch (JsonException) { notes.Add($"{name}: metadata could not be parsed for diagnostics."); }
        }
        foreach (var name in rootFileNames)
        {
            if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("COPYING", StringComparison.OrdinalIgnoreCase)) licenses.Add(new(name, null));
        }
        var requirements = await ReadMetadataAsync(root, "requirements.txt", notes, cancellationToken).ConfigureAwait(false);
        if (requirements is not null)
        {
            var lineNumber = 0;
            foreach (var raw in requirements.Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var comment = line.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) line = line[..comment].TrimEnd();
                var match = Requirement().Match(line);
                if (!match.Success)
                {
                    notes.Add($"requirements.txt:{lineNumber}: unsupported declaration; manual review required (not executed or followed).");
                    continue;
                }
                dependencies.Add(new(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null,
                    $"requirements.txt:{lineNumber}"));
            }
        }
        if (licenses.Count == 0) notes.Add("No supported license declaration or root license file was found; license is unknown.");
        notes.Add("License evidence is unverified and is not legal approval.");
        notes.Add("Dependencies are declarations only; installed versions and transitive dependencies were not checked.");
        notes.Add("Artifact bytes measure listed files, not required RAM/VRAM; resource requirements need runtime profiling.");
        return new(licenses, dependencies, artifacts.Sum(item => item.Size), null, null, notes);
    }

    private static async Task<string?> ReadMetadataAsync(string root, string name, List<string> notes, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var buffer = new byte[256 * 1024 + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count == buffer.Length) { notes.Add($"{name}: exceeds diagnostic metadata limit; manual review required."); return null; }
        return Encoding.UTF8.GetString(buffer, 0, count);
    }

    // Deliberately a non-executing subset, not a general YAML loader: only a top-level
    // license scalar or a flat list of simple identifiers in a closed front-matter block.
    private static void ReadReadmeLicenses(string text, string name, List<ModelLicenseEvidence> licenses, List<string> notes)
    {
        var lines = text.TrimStart('\uFEFF').Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---") return;
        var end = Array.FindIndex(lines, 1, line => line.TrimEnd('\r') is "---" or "...");
        if (end < 0) { notes.Add($"{name}: unclosed front matter; manual license review required."); return; }
        var found = false;
        var invalid = false;
        var inList = false;
        var inLicense = false;
        var listIndent = -1;
        var parsed = new List<ModelLicenseEvidence>();
        for (var i = 1; i < end; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var rootLicense = ReadmeLicenseKey().Match(raw);
            if (rootLicense.Success)
            {
                if (found) { invalid = true; break; }
                found = true;
                inLicense = true;
                var value = rootLicense.Groups[1].Value.Trim();
                if (value.Length == 0 || value.StartsWith('#')) { inList = true; continue; }
                if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    var items = value[1..^1].Split(',');
                    foreach (var item in items)
                        if (!Add(item.Trim(), i + 1)) invalid = true;
                }
                else if (!Add(value, i + 1)) invalid = true;
                continue;
            }
            if (!inLicense) continue;
            if (!inList)
            {
                if (char.IsWhiteSpace(raw[0]) || trimmed.StartsWith("-", StringComparison.Ordinal)) invalid = true;
                else inLicense = false;
                continue;
            }
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var indent = raw.Length - raw.TrimStart(' ').Length;
                if (raw.Contains('\t') || (listIndent >= 0 && listIndent != indent)) { invalid = true; break; }
                listIndent = indent;
                if (!Add(trimmed[2..].Trim(), i + 1)) invalid = true;
            }
            else if (!char.IsWhiteSpace(raw[0]) && raw.Contains(':')) { inList = false; inLicense = false; }
            else invalid = true;
        }
        if (!found) return;
        if (invalid || parsed.Count == 0)
            notes.Add($"{name}: unsupported or ambiguous license front matter; manual review required (not executed or followed).");
        else licenses.AddRange(parsed);

        bool Add(string value, int line)
        {
            var match = ReadmeLicenseScalar().Match(value);
            if (!match.Success) return false;
            parsed.Add(new($"{name}:{line}", match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value));
            return true;
        }
    }

    [GeneratedRegex("\\A(?:([A-Za-z0-9][A-Za-z0-9.+_-]{0,127})|'([A-Za-z0-9][A-Za-z0-9.+_-]{0,127})'|\"([A-Za-z0-9][A-Za-z0-9.+_-]{0,127})\")(?:[ \\t]+#[^\\r\\n]*)?\\z")]
    private static partial Regex ReadmeLicenseScalar();

    [GeneratedRegex("\\A(?:license|'license'|\"license\")[ \\t]*:(?=[ \\t]|$)(.*)\\z")]
    private static partial Regex ReadmeLicenseKey();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}\z")]
    private static partial Regex LicenseIdentifier();

    [GeneratedRegex(@"\A([A-Za-z0-9][A-Za-z0-9._-]*)(?:\s*((?:(?:==|!=|>=|<=|~=|>|<)\s*[A-Za-z0-9.*+_-]+)(?:\s*,\s*(?:==|!=|>=|<=|~=|>|<)\s*[A-Za-z0-9.*+_-]+)*))?\z")]
    private static partial Regex Requirement();
}
