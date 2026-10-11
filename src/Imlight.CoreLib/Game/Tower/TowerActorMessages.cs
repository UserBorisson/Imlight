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
    public string ResolvedDestinationZone { get; init; }
    public string ResolvedDestinationLocation { get; init; }
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

internal sealed class TowerFloorClearedMessage : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 101;
    public ulong WizardId { get; init; }
    public int Floor { get; init; }
    public bool Completed { get; init; }

    internal string GetClientMessage()
        => Completed
            ? "Infinite Golem Tower complete! You cleared all 10 floors."
            : Floor == 5
                ? "Infinite Golem Tower: floor 5 cleared. Exit to the Court and reenter to begin floor 6."
                : $"Infinite Golem Tower: floor {Floor} cleared. Use the stairs to continue to floor {Floor + 1}.";
}

internal sealed class TowerCombatAbortedMessage : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 106;
    public ulong WizardId { get; init; }
}
