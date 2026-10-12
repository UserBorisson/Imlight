using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Internal combat-to-recipient request carrying a stable idempotency key.</summary>
internal sealed class TowerAwardDeliveryMessage : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 106;
    public ulong WizardId { get; init; }
    public string AwardId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public int ExperiencePoints { get; init; }
    public long Floor { get; init; }
    public int Tickets { get; init; }
    public long HighestFloorCleared { get; init; }
    public uint[] ItemTemplateIds { get; init; } = [];
    public int RetryCount { get; init; }
}
