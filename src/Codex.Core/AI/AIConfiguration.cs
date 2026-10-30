namespace Codex.Core.AI;

public class AIConfiguration
{
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string? ModelId { get; set; }

    /// <summary>The client every request goes through, when the caller needs to control where connections may lead.</summary>
    public HttpClient? Http { get; set; }
}

/// <summary>The server-wide fallback image model (the <c>AIImage</c> section); its own type so it
/// can sit in DI next to the chat fallback.</summary>
public class AIImageConfiguration : AIConfiguration;
