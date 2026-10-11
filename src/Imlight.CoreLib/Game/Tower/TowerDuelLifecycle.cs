using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>
/// Tracks the live duel's server-admitted participants and combat-death evidence. A roster slot
/// is marked dead only by the duel's resolved-round path, never by actor disappearance.
/// </summary>
internal sealed class TowerEncounterRoster {
    private readonly string _encounterId;
    private readonly int _expectedEnemyCount;
    private readonly Dictionary<int, ulong> _admittedEnemies = [];
    private readonly HashSet<int> _combatDeathEnemySlots = [];
    private readonly HashSet<ulong> _admittedPlayers = [];

    public bool IsComplete => _expectedEnemyCount > 0 && _admittedEnemies.Count == _expectedEnemyCount;
    public int AdmittedPlayerCount => _admittedPlayers.Count;
    public int[] AdmittedEnemySlots => [.. _admittedEnemies.Keys.Order()];
    public int[] CombatDeathEnemySlots => [.. _combatDeathEnemySlots.Order()];

    public TowerEncounterRoster(TowerEncounterManifest manifest) {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.EncounterId)) {
            throw new ArgumentException("A tower encounter id is required.", nameof(manifest));
        }
        if (manifest.Enemies is not { Count: > 0 and <= TowerRunState.MaximumEnemyCount }) {
            throw new ArgumentException("A tower manifest must contain one or two enemies.", nameof(manifest));
        }

        _encounterId = manifest.EncounterId;
        _expectedEnemyCount = manifest.Enemies.Count;
    }

    public bool CanAdmitEnemy(string encounterId, int rosterSlot, ulong actorObjectId)
        => string.Equals(_encounterId, encounterId, StringComparison.Ordinal)
        && rosterSlot >= 0
        && rosterSlot < _expectedEnemyCount
        && actorObjectId != 0
        && !_admittedEnemies.ContainsKey(rosterSlot)
        && !_admittedEnemies.Values.Contains(actorObjectId);

    public bool AdmitEnemy(string encounterId, int rosterSlot, ulong actorObjectId) {
        if (!CanAdmitEnemy(encounterId, rosterSlot, actorObjectId)) { return false; }
        _admittedEnemies.Add(rosterSlot, actorObjectId);
        return true;
    }

    public bool RecordCombatDeath(int rosterSlot, ulong actorObjectId) {
        if (!_admittedEnemies.TryGetValue(rosterSlot, out var admittedActor)
            || admittedActor != actorObjectId
            || _combatDeathEnemySlots.Contains(rosterSlot)) {
            return false;
        }

        _combatDeathEnemySlots.Add(rosterSlot);
        return true;
    }

    public bool CanAdmitPlayer(ulong wizardId)
        => wizardId != 0
        && (_admittedPlayers.Contains(wizardId)
            || _admittedPlayers.Count < TowerRunState.MaximumPartySize);

    public bool AdmitPlayer(ulong wizardId) {
        if (!CanAdmitPlayer(wizardId)) { return false; }
        if (_admittedPlayers.Contains(wizardId)) { return true; }
        return _admittedPlayers.Add(wizardId);
    }

    public TowerEncounterResolution CreateResolution(long authorityEpoch, int playerCount, int alivePlayerCount)
        => new() {
            AuthorityEpoch = authorityEpoch,
            EncounterId = _encounterId,
            PlayerCount = playerCount,
            AlivePlayerCount = alivePlayerCount,
            AdmittedPlayerIds = [.. _admittedPlayers.Order()],
            AdmittedEnemySlots = AdmittedEnemySlots,
            CombatDeathEnemySlots = CombatDeathEnemySlots,
        };
}

internal enum TowerDuelOutcome {
    Victory,
    Defeat,
    TechnicalAbort,
}

internal static class TowerDuelOutcomePolicy {
    public static TowerDuelOutcome Classify(TowerResolutionResult result)
        => result switch {
            TowerResolutionResult.Victory => TowerDuelOutcome.Victory,
            TowerResolutionResult.Defeat => TowerDuelOutcome.Defeat,
            _ => TowerDuelOutcome.TechnicalAbort,
        };
}

internal enum TowerDuelInterruption {
    IntentionalFlee,
    NativeFlee,
    TechnicalAbort,
}

internal static class TowerDuelInterruptionPolicy {
    public static TowerDuelInterruption Classify(bool isTowerEncounter, bool intentional)
        => intentional
            ? TowerDuelInterruption.IntentionalFlee
            : isTowerEncounter
                ? TowerDuelInterruption.TechnicalAbort
                : TowerDuelInterruption.NativeFlee;
}
