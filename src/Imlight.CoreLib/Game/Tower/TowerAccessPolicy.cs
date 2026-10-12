using System;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Opens the two public Tower entrances without changing story quest state.</summary>
public static class TowerAccessPolicy {
    public static bool OpenEntrance(bool towerEnabled, string sourceZone, Trigger trigger) {
        if (!towerEnabled || trigger?.m_results?.m_results is not { Count: 1 } results
            || results[0] is not ResTeleport teleport) { return false; }

        var courtEntrance = string.Equals(sourceZone, "WizardCity/WC_Hub", StringComparison.OrdinalIgnoreCase)
            && string.Equals(teleport.m_destinationZone, TowerZonePaths.EntryZone, StringComparison.OrdinalIgnoreCase);
        var sanctuaryEntrance = string.Equals(sourceZone, TowerZonePaths.EntryZone, StringComparison.OrdinalIgnoreCase)
            && TowerZonePaths.IsSanctuaryPath(teleport.m_destinationZone);
        if (!courtEntrance && !sanctuaryEntrance) { return false; }

        trigger.m_requirements = null;
        return true;
    }
}
