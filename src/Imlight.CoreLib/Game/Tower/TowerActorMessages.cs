using Akka.Actor;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Tower;

internal sealed class TowerInstanceRouteQuery {
    public ZONE_102_PROTOCOL.MSG_ZONETRANSFER Transfer { get; init; }
    public TowerRunState ActivateRun { get; init; }
}

internal sealed class TowerInstanceRouteResult {
    public ZONE_102_PROTOCOL.MSG_ZONETRANSFER Transfer { get; init; }
    public bool HasZone { get; init; }
    public string ErrorMessage { get; init; }
}

internal sealed class TowerInstanceRouteCompleted {
    public IActorRef OriginalSender { get; init; }
    public ZONE_102_PROTOCOL.MSG_ZONETRANSFER OriginalTransfer { get; init; }
    public TowerInstanceRouteResult Response { get; init; }
}

internal sealed class TowerRunChanged {
    public TowerRunState State { get; init; }
}

public sealed class TowerFloorTransferMessage : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 101;
    public ulong WizardId { get; init; }
    public string DestinationZone { get; init; }
    public bool Extracted { get; init; }
    public bool ForceReload { get; init; }
}

internal sealed class TowerCombatAbortedMessage : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 106;
    public ulong WizardId { get; init; }
}
