using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Internal SessionActor message used to reconcile a Raven-committed XP total in memory.</summary>
internal sealed class TowerExperienceCommittedMessage : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 103;
    public ulong WizardId { get; init; }
    public int ExperiencePointsTotal { get; init; }
}
