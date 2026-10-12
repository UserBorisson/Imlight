using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Imlight.CoreLib.Game.Tower;

public enum TowerRunStatus {
    Active,
    Defeated,
    Extracted,
    Completed,
    Abandoned,
}

public enum TowerResolutionResult {
    Victory,
    Defeat,
    EncounterContinues,
    StaleAuthority,
    NotCurrentEncounter,
    InvalidSnapshot,
    RunCompleted,
    RunNotActive,
}

public enum TowerEncounterLifecycle {
    NotStarted,
    InCombat,
    RecoveryPending,
}

/// <summary>
/// Durable state for one Adventure run. The current manifest is materialized once and reused
/// until a floor transition; an attempt generation changes the manifest identity on restart.
/// </summary>
public sealed class TowerRunState {
    public const int FloorCount = 10;
    public const int PhysicalRoomCount = 5;
    public const int MaximumPartySize = 2;
    public const int MaximumEnemyCount = 2;
    public const int RecoveryAllowancePerRun = 1;

    public string RunId { get; set; } = string.Empty;
    public ulong OwnerWizardId { get; set; }
    public ulong RunSeed { get; set; }
    public int CurrentFloor { get; set; } = 1;
    public int HighestFloorCleared { get; set; }
    public int CheckpointFloor { get; set; }
    public int AttemptGeneration { get; set; } = 1;
    public int EncounterRecoveryGeneration { get; set; }
    public int RecoveryAllowanceRemaining { get; set; } = RecoveryAllowancePerRun;
    public bool RecoveryResourcesPending { get; set; }
    public long AuthorityEpoch { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public string AuthorityProcessIncarnation { get; set; } = string.Empty;
    public string AuthorityLeaseId { get; set; } = string.Empty;
    public TowerAuthorityTakeoverProof LastTakeoverProof { get; set; }
    public TowerRunStatus Status { get; set; } = TowerRunStatus.Active;
    public TowerEncounterLifecycle EncounterLifecycle { get; set; } = TowerEncounterLifecycle.NotStarted;
    public ulong[] AdmittedPlayerIds { get; set; } = [];
    public int[] AdmittedEnemySlots { get; set; } = [];
    public TowerEncounterManifest CurrentManifest { get; set; }
    public List<TowerWizardResourceSnapshot> FloorEntryResources { get; set; } = [];
    public List<TowerPendingAward> PendingAwards { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public TowerRunState() { }

    public TowerRunState Copy() => new() {
        RunId = RunId,
        OwnerWizardId = OwnerWizardId,
        RunSeed = RunSeed,
        CurrentFloor = CurrentFloor,
        HighestFloorCleared = HighestFloorCleared,
        CheckpointFloor = CheckpointFloor,
        AttemptGeneration = AttemptGeneration,
        EncounterRecoveryGeneration = EncounterRecoveryGeneration,
        RecoveryAllowanceRemaining = RecoveryAllowanceRemaining,
        RecoveryResourcesPending = RecoveryResourcesPending,
        AuthorityEpoch = AuthorityEpoch,
        Revision = Revision,
        AuthorityProcessIncarnation = AuthorityProcessIncarnation,
        AuthorityLeaseId = AuthorityLeaseId,
        LastTakeoverProof = LastTakeoverProof?.Copy(),
        Status = Status,
        EncounterLifecycle = EncounterLifecycle,
        AdmittedPlayerIds = [.. (AdmittedPlayerIds ?? [])],
        AdmittedEnemySlots = [.. (AdmittedEnemySlots ?? [])],
        CurrentManifest = CurrentManifest is null ? null : new TowerEncounterManifest {
            EncounterId = CurrentManifest.EncounterId,
            AttemptGeneration = CurrentManifest.AttemptGeneration,
            EncounterRecoveryGeneration = CurrentManifest.EncounterRecoveryGeneration,
            Floor = CurrentManifest.Floor,
            PhysicalRoomNumber = CurrentManifest.PhysicalRoomNumber,
            Seed = CurrentManifest.Seed,
            IsBoss = CurrentManifest.IsBoss,
            Enemies = [.. CurrentManifest.Enemies.Select(enemy => new TowerEnemySpawn {
                EncounterId = enemy.EncounterId,
                RosterSlot = enemy.RosterSlot,
                ArchetypeId = enemy.ArchetypeId,
                RoleId = enemy.RoleId,
                SchoolId = enemy.SchoolId,
                Level = enemy.Level,
                HealthMultiplier = enemy.HealthMultiplier,
                SpellNames = [.. enemy.SpellNames],
            })],
        },
        FloorEntryResources = [.. (FloorEntryResources ?? []).Select(snapshot => snapshot.Copy())],
        PendingAwards = [.. (PendingAwards ?? []).Select(award => new TowerPendingAward {
            RunId = award.RunId,
            WizardId = award.WizardId,
            AwardId = award.AwardId,
            ExperiencePoints = award.ExperiencePoints,
            HighestFloorCleared = award.HighestFloorCleared,
            ItemTemplateIds = [.. award.ItemTemplateIds],
        })],
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
    };

    public static TowerRunState Create(string runId, ulong ownerWizardId, ulong runSeed)
        => Create(runId, ownerWizardId, runSeed, TowerProcessIncarnation.Current);

    internal static TowerRunState Create(string runId, ulong ownerWizardId, ulong runSeed, string processIncarnation) {
        if (string.IsNullOrWhiteSpace(runId)) { throw new ArgumentException("A run id is required.", nameof(runId)); }
        if (ownerWizardId == 0) { throw new ArgumentOutOfRangeException(nameof(ownerWizardId)); }
        if (string.IsNullOrWhiteSpace(processIncarnation)) { throw new ArgumentException("A process incarnation is required.", nameof(processIncarnation)); }

        return new TowerRunState {
            RunId = runId,
            OwnerWizardId = ownerWizardId,
            RunSeed = runSeed,
            AuthorityProcessIncarnation = processIncarnation,
            AuthorityLeaseId = Guid.NewGuid().ToString("N"),
        };
    }

    public TowerEncounterManifest BuildCurrentManifest() {
        if (CurrentManifest is not null) { return CurrentManifest; }
        if (Status != TowerRunStatus.Active || CurrentFloor is < 1 or > FloorCount) {
            throw new InvalidOperationException("A manifest can only be created for an active floor.");
        }

        CurrentManifest = TowerEncounterFactory.Create(RunId, RunSeed, AttemptGeneration,
            EncounterRecoveryGeneration, CurrentFloor);
        UpdatedUtc = DateTime.UtcNow;
        return CurrentManifest;
    }

    /// <summary>Persists the complete server-admitted roster before duel gameplay begins.</summary>
    public bool TryBeginEncounter(ulong[] admittedPlayerIds, int[] admittedEnemySlots) {
        if (Status != TowerRunStatus.Active || CurrentManifest is null
            || admittedPlayerIds is not { Length: > 0 and <= MaximumPartySize }
            || admittedPlayerIds.Any(id => id == 0)
            || admittedPlayerIds.Distinct().Count() != admittedPlayerIds.Length
            || admittedEnemySlots is null
            || admittedEnemySlots.Distinct().Count() != admittedEnemySlots.Length
            || admittedEnemySlots.Length != CurrentManifest.Enemies.Count
            || !admittedEnemySlots.Order().SequenceEqual(Enumerable.Range(0, CurrentManifest.Enemies.Count))) {
            return false;
        }

        if (EncounterLifecycle == TowerEncounterLifecycle.InCombat) {
            return (AdmittedPlayerIds ?? []).Order().SequenceEqual(admittedPlayerIds.Order())
                && (AdmittedEnemySlots ?? []).Order().SequenceEqual(admittedEnemySlots.Order());
        }

        if (EncounterLifecycle is not (TowerEncounterLifecycle.NotStarted or TowerEncounterLifecycle.RecoveryPending)) {
            return false;
        }

        AdmittedPlayerIds = [.. admittedPlayerIds.Order()];
        AdmittedEnemySlots = [.. admittedEnemySlots.Order()];
        EncounterLifecycle = TowerEncounterLifecycle.InCombat;
        UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    public bool CaptureFloorEntryResource(ulong wizardId, int health, int mana) {
        if (wizardId == 0 || Status != TowerRunStatus.Active || CurrentManifest is null
            || health < 0 || mana < 0 || (FloorEntryResources ?? []).Any(snapshot =>
                snapshot.WizardId == wizardId && snapshot.Floor == CurrentFloor
                && snapshot.AttemptGeneration == AttemptGeneration)) {
            return false;
        }

        FloorEntryResources ??= [];
        FloorEntryResources.Add(new TowerWizardResourceSnapshot {
            WizardId = wizardId,
            Floor = CurrentFloor,
            AttemptGeneration = AttemptGeneration,
            EncounterId = CurrentManifest.EncounterId,
            Health = health,
            Mana = mana,
        });
        UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    public bool TryGetPendingRecoveryResources(ulong wizardId, out TowerWizardResourceSnapshot snapshot) {
        snapshot = null;
        if (!RecoveryResourcesPending || wizardId == 0) { return false; }
        snapshot = (FloorEntryResources ?? []).FirstOrDefault(item => item.WizardId == wizardId
            && item.Floor == CurrentFloor && item.AttemptGeneration == AttemptGeneration && !item.WasRestored);
        return snapshot is not null;
    }

    public bool MarkRecoveryResourcesRestored(ulong wizardId) {
        if (!TryGetPendingRecoveryResources(wizardId, out var snapshot)) { return false; }
        snapshot.WasRestored = true;
        RecoveryResourcesPending = (FloorEntryResources ?? []).Any(item =>
            item.Floor == CurrentFloor && item.AttemptGeneration == AttemptGeneration && !item.WasRestored);
        UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    public bool ScheduleEncounterRecovery() {
        if (Status != TowerRunStatus.Active || EncounterLifecycle != TowerEncounterLifecycle.InCombat) { return false; }

        if (RecoveryAllowanceRemaining <= 0) {
            Status = TowerRunStatus.Defeated;
            EncounterLifecycle = TowerEncounterLifecycle.NotStarted;
            CurrentManifest = null;
            AdmittedPlayerIds = [];
            AdmittedEnemySlots = [];
            RecoveryResourcesPending = false;
            UpdatedUtc = DateTime.UtcNow;
            return false;
        }

        RecoveryAllowanceRemaining--;
        EncounterRecoveryGeneration = checked(EncounterRecoveryGeneration + 1);
        EncounterLifecycle = TowerEncounterLifecycle.RecoveryPending;
        AdmittedPlayerIds = [];
        AdmittedEnemySlots = [];
        CurrentManifest = null;
        RecoveryResourcesPending = (FloorEntryResources ?? []).Any(snapshot =>
            snapshot.Floor == CurrentFloor && snapshot.AttemptGeneration == AttemptGeneration);
        foreach (var snapshot in FloorEntryResources ?? []) {
            if (snapshot.Floor == CurrentFloor && snapshot.AttemptGeneration == AttemptGeneration) {
                snapshot.WasRestored = false;
            }
        }
        UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    public TowerResolutionResult ApplyResolution(TowerEncounterResolution resolution) {
        ArgumentNullException.ThrowIfNull(resolution);

        if (Status == TowerRunStatus.Completed) { return TowerResolutionResult.RunCompleted; }
        if (Status != TowerRunStatus.Active) { return TowerResolutionResult.RunNotActive; }
        if (resolution.AuthorityEpoch != AuthorityEpoch) { return TowerResolutionResult.StaleAuthority; }
        if (EncounterLifecycle != TowerEncounterLifecycle.InCombat) { return TowerResolutionResult.InvalidSnapshot; }

        var manifest = BuildCurrentManifest();
        if (!string.Equals(resolution.EncounterId, manifest.EncounterId, StringComparison.Ordinal)) {
            return TowerResolutionResult.NotCurrentEncounter;
        }

        if (resolution.PlayerCount is < 1 or > MaximumPartySize
            || resolution.AlivePlayerCount < 0
            || resolution.AlivePlayerCount > resolution.PlayerCount
            || resolution.AdmittedPlayerIds is not { Length: > 0 and <= MaximumPartySize }
            || resolution.AdmittedPlayerIds.Distinct().Count() != resolution.AdmittedPlayerIds.Length
            || resolution.AdmittedPlayerIds.Any(id => id == 0)
            || resolution.AdmittedPlayerIds.Length != resolution.PlayerCount
            || !(AdmittedPlayerIds ?? []).Order().SequenceEqual(resolution.AdmittedPlayerIds.Order())
            || resolution.AdmittedEnemySlots is null
            || resolution.CombatDeathEnemySlots is null
            || resolution.AdmittedEnemySlots.Distinct().Count() != resolution.AdmittedEnemySlots.Length
            || resolution.AdmittedEnemySlots.Length != manifest.Enemies.Count
            || !(AdmittedEnemySlots ?? []).Order().SequenceEqual(resolution.AdmittedEnemySlots.Order())
            || !resolution.AdmittedEnemySlots.Order().SequenceEqual(Enumerable.Range(0, manifest.Enemies.Count))
            || resolution.CombatDeathEnemySlots.Distinct().Count() != resolution.CombatDeathEnemySlots.Length
            || resolution.CombatDeathEnemySlots.Any(slot => !resolution.AdmittedEnemySlots.Contains(slot))) {
            return TowerResolutionResult.InvalidSnapshot;
        }

        // Player loss wins ties. A simultaneous final hit is a defeat and earns no clear/reward.
        if (resolution.AlivePlayerCount == 0) {
            Status = TowerRunStatus.Defeated;
            EncounterLifecycle = TowerEncounterLifecycle.NotStarted;
            AdmittedPlayerIds = [];
            AdmittedEnemySlots = [];
            UpdatedUtc = DateTime.UtcNow;
            return TowerResolutionResult.Defeat;
        }

        if (resolution.CombatDeathEnemySlots.Length != manifest.Enemies.Count) { return TowerResolutionResult.EncounterContinues; }

        HighestFloorCleared = Math.Max(HighestFloorCleared, CurrentFloor);
        if (CurrentFloor is 5 or 10) { CheckpointFloor = CurrentFloor; }

        if (CurrentFloor == FloorCount) {
            Status = TowerRunStatus.Completed;
        }
        else {
            CurrentFloor++;
            CurrentManifest = null;
        }

        FloorEntryResources.Clear();
        RecoveryResourcesPending = false;
        EncounterRecoveryGeneration = 0;
        EncounterLifecycle = TowerEncounterLifecycle.NotStarted;
        AdmittedPlayerIds = [];
        AdmittedEnemySlots = [];
        UpdatedUtc = DateTime.UtcNow;
        return TowerResolutionResult.Victory;
    }

    /// <summary>Increments the fencing token when a recovered actor becomes the sole writer.</summary>
    public long ReacquireAuthority() {
        checked { AuthorityEpoch++; }
        UpdatedUtc = DateTime.UtcNow;
        return AuthorityEpoch;
    }

    internal bool AcquireAuthority(string processIncarnation) {
        if (string.IsNullOrWhiteSpace(processIncarnation)
            || Status is TowerRunStatus.Completed or TowerRunStatus.Abandoned) {
            return false;
        }

        var fromEpoch = AuthorityEpoch;
        var fromRevision = Revision;
        var fromIncarnation = AuthorityProcessIncarnation;
        var processChanged = !string.Equals(fromIncarnation, processIncarnation, StringComparison.Ordinal);
        var recoversActiveEncounter = processChanged && Status == TowerRunStatus.Active
            && EncounterLifecycle == TowerEncounterLifecycle.InCombat;
        var recoveryAllowanceConsumed = false;

        if (recoversActiveEncounter) {
            if (RecoveryAllowanceRemaining > 0) {
                RecoveryAllowanceRemaining--;
                recoveryAllowanceConsumed = true;
                EncounterRecoveryGeneration = checked(EncounterRecoveryGeneration + 1);
                EncounterLifecycle = TowerEncounterLifecycle.RecoveryPending;
                CurrentManifest = null;
                RecoveryResourcesPending = (FloorEntryResources ?? []).Any(snapshot =>
                    snapshot.Floor == CurrentFloor && snapshot.AttemptGeneration == AttemptGeneration);
                foreach (var snapshot in FloorEntryResources ?? []) {
                    if (snapshot.Floor == CurrentFloor && snapshot.AttemptGeneration == AttemptGeneration) {
                        snapshot.WasRestored = false;
                    }
                }
            }
            else {
                Status = TowerRunStatus.Defeated;
                EncounterLifecycle = TowerEncounterLifecycle.NotStarted;
                CurrentManifest = null;
                AdmittedPlayerIds = [];
                AdmittedEnemySlots = [];
                RecoveryResourcesPending = false;
            }
        }

        var leaseId = Guid.NewGuid().ToString("N");
        AuthorityEpoch = checked(AuthorityEpoch + 1);
        AuthorityProcessIncarnation = processIncarnation;
        AuthorityLeaseId = leaseId;
        Revision = checked(Revision + 1);
        UpdatedUtc = DateTime.UtcNow;
        LastTakeoverProof = new TowerAuthorityTakeoverProof {
            FromEpoch = fromEpoch,
            ToEpoch = AuthorityEpoch,
            PriorRevision = fromRevision,
            CommittedRevision = Revision,
            FromProcessIncarnation = fromIncarnation,
            ToProcessIncarnation = processIncarnation,
            LeaseId = leaseId,
            RecoveryAllowanceConsumed = recoveryAllowanceConsumed,
        };
        return true;
    }

    /// <summary>
    /// Starts a new attempt after defeat or extraction, resuming at the first floor that has not
    /// been cleared (or floor 1 when the run has no cleared floors).
    /// </summary>
    public void RestartFromCheckpoint() => RestartFromCheckpoint(authorityAlreadyReacquired: false);

    internal void RestartFromCheckpoint(bool authorityAlreadyReacquired) {
        if (Status == TowerRunStatus.Completed || Status == TowerRunStatus.Abandoned) {
            throw new InvalidOperationException("A terminal run cannot be restarted.");
        }

        checked {
            AttemptGeneration++;
            if (!authorityAlreadyReacquired) { AuthorityEpoch++; }
        }
        CurrentFloor = Math.Min(HighestFloorCleared + 1, FloorCount);
        Status = TowerRunStatus.Active;
        EncounterRecoveryGeneration = 0;
        EncounterLifecycle = TowerEncounterLifecycle.NotStarted;
        AdmittedPlayerIds = [];
        AdmittedEnemySlots = [];
        RecoveryResourcesPending = false;
        FloorEntryResources.Clear();
        CurrentManifest = null;
        UpdatedUtc = DateTime.UtcNow;
    }
}

public sealed class TowerEncounterResolution {
    public long AuthorityEpoch { get; set; }
    public string EncounterId { get; set; } = string.Empty;
    public int PlayerCount { get; set; }
    public int AlivePlayerCount { get; set; }
    public ulong[] AdmittedPlayerIds { get; set; } = [];
    public int[] AdmittedEnemySlots { get; set; } = [];
    public int[] CombatDeathEnemySlots { get; set; } = [];
}

public sealed class TowerWizardResourceSnapshot {
    public ulong WizardId { get; set; }
    public int Floor { get; set; }
    public int AttemptGeneration { get; set; }
    public string EncounterId { get; set; } = string.Empty;
    public int Health { get; set; }
    public int Mana { get; set; }
    public bool WasRestored { get; set; }

    internal TowerWizardResourceSnapshot Copy() => new() {
        WizardId = WizardId,
        Floor = Floor,
        AttemptGeneration = AttemptGeneration,
        EncounterId = EncounterId,
        Health = Health,
        Mana = Mana,
        WasRestored = WasRestored,
    };
}

public sealed class TowerAuthorityTakeoverProof {
    public long FromEpoch { get; set; }
    public long ToEpoch { get; set; }
    public long PriorRevision { get; set; }
    public long CommittedRevision { get; set; }
    public string FromProcessIncarnation { get; set; } = string.Empty;
    public string ToProcessIncarnation { get; set; } = string.Empty;
    public string LeaseId { get; set; } = string.Empty;
    public bool RecoveryAllowanceConsumed { get; set; }

    internal TowerAuthorityTakeoverProof Copy() => new() {
        FromEpoch = FromEpoch,
        ToEpoch = ToEpoch,
        PriorRevision = PriorRevision,
        CommittedRevision = CommittedRevision,
        FromProcessIncarnation = FromProcessIncarnation,
        ToProcessIncarnation = ToProcessIncarnation,
        LeaseId = LeaseId,
        RecoveryAllowanceConsumed = RecoveryAllowanceConsumed,
    };
}

/// <summary>Durable reward outbox entry committed with the floor resolution.</summary>
public sealed class TowerPendingAward {
    public string RunId { get; set; } = string.Empty;
    public ulong WizardId { get; set; }
    public string AwardId { get; set; } = string.Empty;
    public int ExperiencePoints { get; set; }
    public int HighestFloorCleared { get; set; }
    public uint[] ItemTemplateIds { get; set; } = [];
}

public sealed class TowerEncounterManifest {
    public string EncounterId { get; set; } = string.Empty;
    public int AttemptGeneration { get; set; }
    public int EncounterRecoveryGeneration { get; set; }
    public int Floor { get; set; }
    public int PhysicalRoomNumber { get; set; }
    public ulong Seed { get; set; }
    public bool IsBoss { get; set; }
    public List<TowerEnemySpawn> Enemies { get; set; } = [];
}

public sealed class TowerEnemySpawn {
    public string EncounterId { get; set; } = string.Empty;
    public int RosterSlot { get; set; }
    public string ArchetypeId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string SchoolId { get; set; } = string.Empty;
    public int Level { get; set; }
    public float HealthMultiplier { get; set; }
    public string[] SpellNames { get; set; } = [];
}

/// <summary>Immutable server-authored definitions used to create seeded floor manifests.</summary>
public sealed record TowerEnemyArchetype(
    string Id,
    string RoleId,
    string SchoolId,
    int BaseLevel,
    float HealthMultiplier,
    IReadOnlyList<string> SpellNames);

public static class TowerContentCatalog {
    private static readonly TowerEnemyArchetype[] s_regularArchetypes = [
        new("iron-ward", "bulwark", "Balance", 12, 1.35f, Array.AsReadOnly(["Cyclops", "Blood Bat", "Scarab"])),
        new("rune-weaver", "controller", "Myth", 12, 1.05f, Array.AsReadOnly(["Blood Bat", "Cyclops", "Troll"])),
        new("forge-adept", "striker", "Fire", 12, 0.95f, Array.AsReadOnly(["Firecat", "Imp", "Sunbird"])),
    ];

    public static IReadOnlyList<TowerEnemyArchetype> RegularArchetypes => Array.AsReadOnly(s_regularArchetypes);

    public static TowerEnemyArchetype FloorFiveBoss { get; } =
        new("golem-warden", "boss", "Balance", 16, 2.4f, Array.AsReadOnly(["Cyclops", "Blood Bat", "Scarab", "Troll"]));

    public static TowerEnemyArchetype FloorTenBoss { get; } =
        new("tower-heart", "boss", "Myth", 20, 3.0f, Array.AsReadOnly(["Cyclops", "Troll", "Minotaur", "Humongofrog"]));
}

internal static class TowerEncounterFactory {
    public static TowerEncounterManifest Create(string runId, ulong runSeed, int attemptGeneration,
                                                int encounterRecoveryGeneration, int floor) {
        var seed = TowerSeed.Derive(runSeed, attemptGeneration, floor + (encounterRecoveryGeneration * 7919));
        var random = new TowerRandom(seed);
        var isBoss = floor is 5 or 10;
        var enemyCount = floor == 10 ? 2 : isBoss ? 1 : random.Next(1, TowerRunState.MaximumEnemyCount + 1);
        var encounterId = $"{runId}/attempt-{attemptGeneration}/floor-{floor}/recovery-{encounterRecoveryGeneration}";
        var enemies = new List<TowerEnemySpawn>(enemyCount);

        for (var slot = 0; slot < enemyCount; slot++) {
            var archetype = floor switch {
                5 => TowerContentCatalog.FloorFiveBoss,
                10 when slot == 0 => TowerContentCatalog.FloorTenBoss,
                10 => TowerContentCatalog.RegularArchetypes[random.Next(0, TowerContentCatalog.RegularArchetypes.Count)],
                _ => TowerContentCatalog.RegularArchetypes[random.Next(0, TowerContentCatalog.RegularArchetypes.Count)],
            };

            enemies.Add(new TowerEnemySpawn {
                EncounterId = encounterId,
                RosterSlot = slot,
                ArchetypeId = archetype.Id,
                RoleId = archetype.RoleId,
                SchoolId = archetype.SchoolId,
                Level = archetype.BaseLevel + Math.Max(0, floor - 1),
                HealthMultiplier = archetype.HealthMultiplier + ((floor - 1) * 0.025f),
                SpellNames = [.. archetype.SpellNames],
            });
        }

        return new TowerEncounterManifest {
            EncounterId = encounterId,
            AttemptGeneration = attemptGeneration,
            EncounterRecoveryGeneration = encounterRecoveryGeneration,
            Floor = floor,
            PhysicalRoomNumber = ((floor - 1) % TowerRunState.PhysicalRoomCount) + 1,
            Seed = seed,
            IsBoss = isBoss,
            Enemies = enemies,
        };
    }
}

internal static class TowerProcessIncarnation {
    public static string Current { get; } = Guid.NewGuid().ToString("N");
}

internal static class TowerSeed {
    public static ulong Derive(ulong seed, int attempt, int floor) {
        var input = Encoding.UTF8.GetBytes($"{seed:x16}:{attempt}:{floor}");
        var digest = SHA256.HashData(input);
        return BitConverter.ToUInt64(digest, 0);
    }
}

internal sealed class TowerRandom(ulong seed) {
    private ulong _state = seed;

    private ulong NextUInt64() {
        _state += 0x9E3779B97F4A7C15UL;
        var value = _state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public int Next(int minInclusive, int maxExclusive) {
        if (minInclusive >= maxExclusive) { throw new ArgumentOutOfRangeException(nameof(maxExclusive)); }
        var range = (uint) (maxExclusive - minInclusive);
        return minInclusive + (int) (NextUInt64() % range);
    }
}
