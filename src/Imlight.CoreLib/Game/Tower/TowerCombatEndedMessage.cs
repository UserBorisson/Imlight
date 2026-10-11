using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Clears combat session state after a tower duel that did not clear the logical floor.</summary>
internal sealed class TowerCombatEndedMessage : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 106;
    public ulong WizardId { get; init; }
}
