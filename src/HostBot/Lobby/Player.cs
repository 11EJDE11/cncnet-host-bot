namespace HostBot.Lobby;

/// <summary>One human in the room, mirroring the client's PlayerInfo.</summary>
public sealed class Player
{
    public string Nick { get; set; } = "";
    public string? Ident { get; set; }
    public string? Host { get; set; }
    public int SideId { get; set; }
    public int ColorId { get; set; }
    public int StartingLocation { get; set; }
    public int TeamId { get; set; }
    public bool Ready { get; set; }
    public bool AutoReady { get; set; }
    public bool HashReceived { get; set; }
    public string? FileHash { get; set; }
    public bool IsInGame { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public bool WantsNextMap { get; set; }
    /// <summary>When the player last became not-ready while the room was full (ready-kick timer base).</summary>
    public DateTime? UnreadySince { get; set; }
    public DateTime? ReadyWarnedAt { get; set; }
    /// <summary>Set when the player is in the room through an !invite whitelist entry (they cannot invite others).</summary>
    public string? InvitedBy { get; set; }
    /// <summary>Times this player un-readied while a start countdown was running.</summary>
    public int CancelledStarts { get; set; }

    /// <summary>PO packing: team | start &lt;&lt; 8 | color &lt;&lt; 16 | side &lt;&lt; 24.</summary>
    public int PackedOptions => TeamId | StartingLocation << 8 | ColorId << 16 | SideId << 24;

    public int ReadyStatus(bool mapInvalid) => AutoReady && !IsInGame && !mapInvalid ? 2 : (Ready ? 1 : 0);
}
