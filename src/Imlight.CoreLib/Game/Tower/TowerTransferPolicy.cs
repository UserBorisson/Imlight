using System;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Server-owned transfer boundary for exactly the five physical Tower rooms.</summary>
public static class TowerTransferPolicy {
    public static bool IsEnabledTowerRoomRequest(ZONE_102_PROTOCOL.MSG_ZONETRANSFER transfer, bool towerEnabled)
        => towerEnabled && transfer is not null && TowerZonePaths.IsRoomPath(transfer.DestinationZone);

    public static bool IsTowerExtractionRequest(bool towerEnabled, string currentZone, string destinationZone)
        => towerEnabled && TowerZonePaths.IsRoomPath(currentZone) && !TowerZonePaths.IsRoomPath(destinationZone);

    public static bool TryBindAuthenticatedOwner(
        ZONE_102_PROTOCOL.MSG_ZONETRANSFER transfer,
        ulong authenticatedWizardId,
        out ZONE_102_PROTOCOL.MSG_ZONETRANSFER boundTransfer) {
        boundTransfer = null;
        if (transfer is null || !TowerZonePaths.IsRoomPath(transfer.DestinationZone)
            || authenticatedWizardId == 0
            || (transfer.OwnerCharId != 0 && transfer.OwnerCharId != authenticatedWizardId)) {
            return false;
        }

        boundTransfer = Copy(transfer);
        boundTransfer.OwnerCharId = authenticatedWizardId;
        boundTransfer.IsPrivate = true;
        return true;
    }

    public static ZONE_102_PROTOCOL.MSG_ZONETRANSFER ApplyResolvedDestination(
        ZONE_102_PROTOCOL.MSG_ZONETRANSFER transfer, ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP response) {
        ArgumentNullException.ThrowIfNull(transfer);
        var resolved = Copy(transfer);
        if (!string.IsNullOrWhiteSpace(response?.ResolvedDestinationZone)) {
            resolved.DestinationZone = response.ResolvedDestinationZone;
        }
        if (!string.IsNullOrWhiteSpace(response?.ResolvedDestinationLocation)) {
            resolved.DestinationLocation = response.ResolvedDestinationLocation;
        }
        return resolved;
    }

    public static ZONE_102_PROTOCOL.MSG_ZONETRANSFER Copy(ZONE_102_PROTOCOL.MSG_ZONETRANSFER original) {
        ArgumentNullException.ThrowIfNull(original);
        return new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = original.DestinationZone,
            DestinationLocation = original.DestinationLocation,
            SendToClient = original.SendToClient,
            IsPrivate = original.IsPrivate,
            OwnerCharId = original.OwnerCharId,
            ResetInstance = original.ResetInstance,
        };
    }
}
