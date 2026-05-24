using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Federation.Models;

public class RemoteItem
{
    public Guid ServerId { get; set; }

    public string RemoteItemId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int? ProductionYear { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new();

    public long? RunTimeTicks { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Container { get; set; }

    public long? Bitrate { get; set; }

    public string? MediaSourceJson { get; set; }

    public DateTime LastSeenUtc { get; set; }
}
