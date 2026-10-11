using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Fail-closed manifest and roster policy for enabled Tower rooms only.</summary>
public static class TowerZonePolicy {
    private const string TowerCourtExitLocation = "-518.1148,1207.106,36.78398,-0.4086321";

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

    // Unfiltered room-to-room transitions are never allowed in a managed Tower room.
    // The director-approved upstairs route is checked against the live run state per event.
    public static bool IsAllowedTowerRoomResult(object result)
        => result switch {
            null => false,
            ResSpawn or ResDespawn or ResDropTable => false,
            ResTeleport teleport when TowerZonePaths.IsRoomPath(teleport.m_destinationZone) => false,
            _ => true,
        };

    /// <summary>
    /// Replaces the authored down-stair destinations with the preserved Tower extraction route.
    /// Other room-to-room destinations remain suppressed so legacy progression cannot bypass the director.
    /// </summary>
    public static Result ResolveRoomResult(string zonePath, TowerRunState run, string triggerName, Result result) {
        if (result is ResTeleport teleport && TowerZonePaths.IsRoomPath(teleport.m_destinationZone)) {
            if (IsTowerExitTrigger(triggerName)) {
                return teleport with {
                    m_destinationZone = TowerZonePaths.EntryZone,
                    m_destinationLoc = TowerCourtExitLocation,
                };
            }

            if (CanUseDirectorStairs(zonePath, run, triggerName, teleport.m_destinationZone)) {
                return teleport;
            }
            return null;
        }

        return IsAllowedTowerRoomResult(result) ? result : null;
    }

    public static List<Result> ResolveRoomResults(string zonePath, TowerRunState run, string triggerName,
        IEnumerable<Result> results)
        => results?.Select(result => ResolveRoomResult(zonePath, run, triggerName, result))
            .Where(result => result is not null).ToList() ?? [];

    private static bool IsTowerExitTrigger(string triggerName)
        => triggerName is "Teleport location (WC_Golem_2 WC_Golem_1 Exit)"
            or "Teleport location (WC_Golem_3 WC_Golem_2 Exit)"
            or "Teleport location (WC_Golem_4 WC_Golem_3 Exit)";

    private static bool CanUseDirectorStairs(string zonePath, TowerRunState run, string triggerName,
        string destinationZone) {
        if (run?.Status != TowerRunStatus.Active || run.CurrentFloor is < 2 or > TowerRunState.FloorCount
            || triggerName is not ("Teleport location (WC_Golem_2 WC_Golem_3 Entrance)"
                or "Teleport location (WC_Golem_3 WC_Golem_4 Entrance)"
                or "Teleport location (WC_Golem_4 to WC_Golem_5)")) {
            return false;
        }

        var clearedFloor = run.CurrentFloor - 1;
        return string.Equals(zonePath, TowerZonePaths.GetRoomPath(clearedFloor), StringComparison.OrdinalIgnoreCase)
            && string.Equals(destinationZone, TowerZonePaths.GetRoomPath(run.CurrentFloor), StringComparison.OrdinalIgnoreCase);
    }

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
