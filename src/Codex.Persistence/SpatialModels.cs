using System;
using System.Collections.Generic;

namespace Codex.Persistence;

public class LocationConnection
{
    public string ToLocationId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int TravelTimeMinutes { get; set; }
}

public class LocationDocument
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<LocationConnection> Connections { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// The spatial graph aggregate (CAMPAIGN_ARCHITECTURE.md 2): every location and connection for a
/// region lives in this one document, so loading the map - or routing across it - is a single
/// <c>Get</c> plus in-memory graph traversal, never a fan-out of per-location loads.
/// </summary>
public class RegionDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<LocationDocument> Locations { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
