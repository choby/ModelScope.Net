using System.Globalization;
using System.Net;
using System.Text;

namespace ModelScope.Net.Hub;

public sealed partial class ModelScopeHubClient
{
    public async Task<string> ResolveModelRevisionAsync(ModelId modelId, string? revision = null,
        CancellationToken cancellationToken = default)
    {
        revision = ResolveRevision(revision);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsCommitHash(revision)) return revision.ToLowerInvariant();
        var uri = BuildUri($"{Escape(modelId.Owner)}/{Escape(modelId.Name)}.git/info/refs", ("service", "git-upload-pack"));
        using var response = await SendWithRetryAsync(() =>
        {
            var message = CreateRequest(HttpMethod.Get, uri);
            message.Version = HttpVersion.Version11;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            message.Headers.UserAgent.Clear();
            message.Headers.UserAgent.ParseAdd("git/ModelScope.Net");
            return message;
        }, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, modelId, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != "application/x-git-upload-pack-advertisement")
            throw InvalidAdvertisement();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[4 * 1024 * 1024 + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count == bytes.Length) throw InvalidAdvertisement();
        var refs = ParseAdvertisement(bytes.AsSpan(0, count));
        var candidates = revision.StartsWith("refs/", StringComparison.Ordinal)
            ? new[] { revision } : new[] { "refs/heads/" + revision, "refs/tags/" + revision };
        var matches = candidates.Where(refs.ContainsKey).ToArray();
        if (matches.Length == 0)
            throw new ModelScopeException(ModelScopeErrorCode.RevisionNotFound, "The requested model reference was not advertised.");
        if (matches.Length != 1)
            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Ambiguous reference; specify refs/heads/ or refs/tags/ explicitly.");
        var selected = matches[0];
        if (!selected.StartsWith("refs/heads/", StringComparison.Ordinal) && !selected.StartsWith("refs/tags/", StringComparison.Ordinal))
            throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Only branch and tag references are supported.");
        return refs.TryGetValue(selected + "^{}", out var peeled) ? peeled : refs[selected];
    }

    private static Dictionary<string, string> ParseAdvertisement(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadPacket(bytes, ref offset) != "# service=git-upload-pack\n" || ReadPacket(bytes, ref offset) is not null)
            throw InvalidAdvertisement();
        var first = true;
        while (true)
        {
            var packet = ReadPacket(bytes, ref offset);
            if (packet is null) break;
            var capabilityStart = packet.IndexOf('\0');
            if (capabilityStart >= 0)
            {
                if (!first) throw InvalidAdvertisement();
                packet = packet[..capabilityStart];
            }
            first = false;
            var line = packet.TrimEnd('\n');
            if (line.Length < 42 || line[40] != ' ' || !IsCommitHash(line[..40])) throw InvalidAdvertisement();
            var name = line[41..];
            if (name.Any(char.IsWhiteSpace)) throw InvalidAdvertisement();
            if (line[..40].All(c => c == '0')) continue;
            if (!refs.TryAdd(name, line[..40].ToLowerInvariant())) throw InvalidAdvertisement();
        }
        if (offset != bytes.Length) throw InvalidAdvertisement();
        return refs;
    }

    private static string? ReadPacket(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (bytes.Length - offset < 4 ||
            !int.TryParse(Encoding.ASCII.GetString(bytes.Slice(offset, 4)), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var length)) throw InvalidAdvertisement();
        offset += 4;
        if (length == 0) return null;
        if (length < 4 || length > 65520 || length - 4 > bytes.Length - offset) throw InvalidAdvertisement();
        try
        {
            var result = new UTF8Encoding(false, true).GetString(bytes.Slice(offset, length - 4));
            offset += length - 4;
            return result;
        }
        catch (DecoderFallbackException) { throw InvalidAdvertisement(); }
    }

    private static ModelScopeException InvalidAdvertisement() =>
        new(ModelScopeErrorCode.RemoteApiUnavailable, "Invalid or unsupported Git reference advertisement.");
}
