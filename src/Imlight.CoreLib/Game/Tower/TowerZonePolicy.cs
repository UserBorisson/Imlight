using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Fail-closed manifest and roster policy for enabled Tower rooms only.</summary>
public static class TowerZonePolicy {
    public static bool IsEnabledTowerRoom(string path)
        => ConfigurationManager.GetValue("Tower.Enabled", false) && TowerZonePaths.IsRoomPath(path);

    public static bool RequiresProgressResetBeforeFirstCombatEntry(
        string zonePath, TowerRunState run, string triggerName, ulong wizardId)
        => TowerZonePaths.IsSanctuaryPath(zonePath)
            && IsSanctuaryStairsTrigger(triggerName)
            && run is { Status: TowerRunStatus.Active, EncounterLifecycle: TowerEncounterLifecycle.NotStarted,
                CurrentManifest: null }
            && run.OwnerWizardId == wizardId
            && run.ProgressionResetVersion < TowerRunCollection.ActiveRosterResetVersion;

    /// <summary>
    /// Creates the only allowed combat-arena upstairs route: after a committed clear, reload the
    /// same physical room with the next logical floor's manifest.
    /// </summary>
    public static TowerFloorTransferMessage CreateUpperStairTransfer(
        string zonePath, TowerRunState run, string triggerName, ulong wizardId) {
        if (!TowerZonePaths.IsCombatArenaPath(zonePath)
            || !IsSanctuaryStairsTrigger(triggerName)
            || run is not { Status: TowerRunStatus.Active, PendingFloorAdvance: true }
            || run.OwnerWizardId == 0 || run.OwnerWizardId != wizardId
            || run.CurrentManifest is not null
            || run.EncounterLifecycle != TowerEncounterLifecycle.NotStarted
            || run.HighestFloorCleared >= long.MaxValue
            || run.CurrentFloor != run.HighestFloorCleared + 1
            || !run.IsFloorUnlocked(run.CurrentFloor)) {
            return null;
        }

        return new TowerFloorTransferMessage {
            WizardId = wizardId,
            DestinationZone = TowerZonePaths.GetRoomPath(run.CurrentFloor),
            ForceReload = true,
        };
    }

    public static bool IsValidManifest(TowerRunState run, string zonePath) {
        if (run is null || run.Status != TowerRunStatus.Active || run.OwnerWizardId == 0
            || run.CurrentFloor < 1
            || !TowerZonePaths.TryGetRoomNumber(zonePath, out var physicalRoom)) {
            return false;
        }

        if (physicalRoom == TowerRunState.SanctuaryRoomNumber) { return true; }

        var manifest = run.CurrentManifest;
        return manifest is not null
            && !string.IsNullOrWhiteSpace(manifest.EncounterId)
            && manifest.AttemptGeneration == run.AttemptGeneration
            && manifest.EncounterRecoveryGeneration == run.EncounterRecoveryGeneration
            && manifest.Floor == run.CurrentFloor
            && manifest.PhysicalRoomNumber == physicalRoom
            && manifest.Enemies is { Count: 1 }
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
    /// Keeps the private sanctuary and combat rooms inside the owner's Tower instance.
    /// The sanctuary's upstairs trigger enters the selected logical floor; combat exits return to sanctuary.
    /// </summary>
    public static Result ResolveRoomResult(string zonePath, TowerRunState run, string triggerName, Result result) {
        if (result is ResTeleport teleport && TowerZonePaths.IsRoomPath(teleport.m_destinationZone)) {
            if (TowerZonePaths.IsSanctuaryPath(zonePath) && IsSanctuaryStairsTrigger(triggerName)
                && run?.Status == TowerRunStatus.Active && run.IsFloorUnlocked(run.CurrentFloor)) {
                return teleport with { m_destinationZone = TowerZonePaths.GetRoomPath(run.CurrentFloor) };
            }

            if (!TowerZonePaths.IsSanctuaryPath(zonePath) && IsTowerExitTrigger(triggerName)) {
                return teleport with {
                    m_destinationZone = TowerZonePaths.GetSanctuaryPath(),
                    m_destinationLoc = "Start",
                };
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
        => !string.IsNullOrWhiteSpace(triggerName)
            && triggerName.StartsWith("Teleport location (", StringComparison.Ordinal)
            && triggerName.EndsWith(" Exit)", StringComparison.Ordinal);

    private static bool IsSanctuaryStairsTrigger(string triggerName)
        => !string.IsNullOrWhiteSpace(triggerName)
            && triggerName.StartsWith("Teleport location (", StringComparison.Ordinal)
            && triggerName.EndsWith(" Entrance)", StringComparison.Ordinal);

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
