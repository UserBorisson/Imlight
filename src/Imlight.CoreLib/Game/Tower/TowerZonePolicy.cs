using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Fail-closed manifest and roster policy for enabled Tower rooms only.</summary>
public static class TowerZonePolicy {
    public static bool IsEnabledTowerRoom(string path)
        => ConfigurationManager.GetValue("Tower.Enabled", false) && TowerZonePaths.IsRoomPath(path);

    public static bool IsValidManifest(TowerRunState run, string zonePath) {
        if (run is null || run.Status != TowerRunStatus.Active || run.OwnerWizardId == 0
            || run.CurrentFloor is < 1 or > TowerRunState.FloorCount
            || !TowerZonePaths.TryGetRoomNumber(zonePath, out var physicalRoom)) {
            return false;
        }

        var manifest = run.CurrentManifest;
        return manifest is not null
            && !string.IsNullOrWhiteSpace(manifest.EncounterId)
            && manifest.AttemptGeneration == run.AttemptGeneration
            && manifest.EncounterRecoveryGeneration == run.EncounterRecoveryGeneration
            && manifest.Floor == run.CurrentFloor
            && manifest.PhysicalRoomNumber == physicalRoom
            && manifest.Enemies is { Count: > 0 and <= TowerRunState.MaximumEnemyCount }
            && manifest.Enemies.All(enemy => enemy is not null
                && enemy.EncounterId == manifest.EncounterId
                && !string.IsNullOrWhiteSpace(enemy.ArchetypeId))
            && IsCompleteManifestRoster(manifest.Enemies.Count,
                manifest.Enemies.Select(enemy => enemy.RosterSlot),
                manifest.Enemies.Select(enemy => enemy.RosterSlot));
    }

    // Legacy encounter, native loot, and room-to-room results never run inside a managed Tower room.
    // Retain native combat initiation (targeting a manifest mob), legitimate Court exits,
    // and harmless presentation results.
    public static bool IsAllowedTowerRoomResult(object result)
        => result switch {
            null => false,
            ResSpawn or ResDespawn or ResDropTable => false,
            ResTeleport teleport when TowerZonePaths.IsRoomPath(teleport.m_destinationZone) => false,
            _ => true,
        };

    public static bool IsCompleteManifestRoster(int expectedEnemyCount,
        IEnumerable<int> profiledSlots, IEnumerable<int> initializedActorSlots) {
        if (expectedEnemyCount is < 1 or > TowerRunState.MaximumEnemyCount
            || profiledSlots is null || initializedActorSlots is null) {
            return false;
        }

        var expected = Enumerable.Range(0, expectedEnemyCount).ToArray();
        return profiledSlots.Order().SequenceEqual(expected)
            && initializedActorSlots.Order().SequenceEqual(expected);
    }
}
