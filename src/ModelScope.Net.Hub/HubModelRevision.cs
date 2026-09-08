namespace ModelScope.Net.Hub;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HubRevisionKind>))]
public enum HubRevisionKind { Branch, Tag }

/// <summary>A named reference, not a resolved repository Commit.</summary>
public sealed record HubModelRevision(string Name, HubRevisionKind Kind, DateTimeOffset? CreatedAt);
