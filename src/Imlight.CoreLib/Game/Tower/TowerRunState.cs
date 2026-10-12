using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;

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

public enum TowerChallengeMode {
    Normal,
    ScaledChallenge,
}

/// <summary>
/// Durable state for one Adventure run. The current manifest is materialized once and reused
/// until a floor transition; an attempt generation changes the manifest identity on restart.
/// </summary>
public sealed class TowerRunState {
    public const int PhysicalRoomCount = 5;
    public const int SanctuaryRoomNumber = 1;
    public const int CombatRoomNumber = 2;
    public const int CombatRoomCount = 1;
    public const int MaximumPartySize = 1;
    public const int MaximumEnemyCount = 1;
    public const int RecoveryAllowancePerRun = 1;

    public string RunId { get; set; } = string.Empty;
    public ulong OwnerWizardId { get; set; }
    public ulong RunSeed { get; set; }
    public long CurrentFloor { get; set; } = 1;
    public long HighestFloorCleared { get; set; }
    public long CheckpointFloor { get; set; }
    public TowerChallengeMode ChallengeMode { get; set; } = TowerChallengeMode.Normal;
    public int ChallengeReferenceLevel { get; set; } = 1;
    public int AttemptGeneration { get; set; } = 1;
    public int EncounterRecoveryGeneration { get; set; }
    public int RecoveryAllowanceRemaining { get; set; } = RecoveryAllowancePerRun;
    public bool RecoveryResourcesPending { get; set; }
    public bool PendingFloorAdvance { get; set; }
    public string LastClearedEncounterId { get; set; } = string.Empty;
    public int ProgressionResetVersion { get; set; }
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
        ChallengeMode = ChallengeMode,
        ChallengeReferenceLevel = ChallengeReferenceLevel,
        AttemptGeneration = AttemptGeneration,
        EncounterRecoveryGeneration = EncounterRecoveryGeneration,
        RecoveryAllowanceRemaining = RecoveryAllowanceRemaining,
        RecoveryResourcesPending = RecoveryResourcesPending,
        PendingFloorAdvance = PendingFloorAdvance,
        LastClearedEncounterId = LastClearedEncounterId,
        ProgressionResetVersion = ProgressionResetVersion,
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
            ChallengeMode = CurrentManifest.ChallengeMode,
            Enemies = [.. CurrentManifest.Enemies.Select(enemy => new TowerEnemySpawn {
                EncounterId = enemy.EncounterId,
                RosterSlot = enemy.RosterSlot,
                TemplateId = enemy.TemplateId,
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
            Floor = award.Floor,
            Tickets = award.Tickets,
            HighestFloorCleared = award.HighestFloorCleared,
            ItemTemplateIds = [.. award.ItemTemplateIds],
        })],
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
    };

    internal static bool IsAcceptableSuccessor(TowerRunState current, TowerRunState incoming) {
        if (incoming is null) { return false; }
        if (current is null) { return true; }
        if (incoming.OwnerWizardId != current.OwnerWizardId) { return false; }

        if (string.Equals(current.RunId, incoming.RunId, StringComparison.Ordinal)) {
            if (incoming.AuthorityEpoch != current.AuthorityEpoch) {
                return incoming.AuthorityEpoch > current.AuthorityEpoch;
            }
            return incoming.Revision >= current.Revision;
        }

        return incoming.CreatedUtc > current.CreatedUtc;
    }

    public bool IsFloorUnlocked(long floor)
        => floor > 0 && (floor <= HighestFloorCleared
            || (HighestFloorCleared < long.MaxValue && floor == HighestFloorCleared + 1));

    public bool TrySelectFloor(long floor, int wizardLevel) {
        if (Status != TowerRunStatus.Active || EncounterLifecycle != TowerEncounterLifecycle.NotStarted
            || CurrentManifest is not null || !IsFloorUnlocked(floor) || wizardLevel < 1) {
            return false;
        }

        CurrentFloor = floor;
        ChallengeReferenceLevel = Math.Clamp(wizardLevel, 1, TowerDifficultyModel.MaximumLevel);
        PendingFloorAdvance = false;
        UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    public bool TrySetChallengeMode(TowerChallengeMode mode, int wizardLevel) {
        if (Status != TowerRunStatus.Active || EncounterLifecycle != TowerEncounterLifecycle.NotStarted
            || CurrentManifest is not null || !Enum.IsDefined(mode) || wizardLevel < 1) {
            return false;
        }

        ChallengeMode = mode;
        ChallengeReferenceLevel = Math.Clamp(wizardLevel, 1, TowerDifficultyModel.MaximumLevel);
        PendingFloorAdvance = false;
        UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Retires a generated encounter that was never admitted to combat, so returning to the
    /// sanctuary can change its floor or challenge mode without touching an active duel.
    /// </summary>
    public bool TryDiscardUnstartedEncounter() {
        if (Status != TowerRunStatus.Active
            || EncounterLifecycle != TowerEncounterLifecycle.NotStarted
            || CurrentManifest is null) {
            return false;
        }

        CurrentManifest = null;
        AdmittedPlayerIds = [];
        AdmittedEnemySlots = [];
        FloorEntryResources ??= [];
        FloorEntryResources.Clear();
        RecoveryResourcesPending = false;
        UpdatedUtc = DateTime.UtcNow;
        return true;
    }

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
        if (Status != TowerRunStatus.Active || CurrentFloor < 1) {
            throw new InvalidOperationException("A manifest can only be created for an active floor.");
        }

        CurrentManifest = TowerEncounterFactory.Create(RunId, RunSeed, AttemptGeneration,
            EncounterRecoveryGeneration, CurrentFloor, HighestFloorCleared, ChallengeMode, ChallengeReferenceLevel);
        PendingFloorAdvance = false;
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
            PendingFloorAdvance = false;
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
            PendingFloorAdvance = false;
            AdmittedPlayerIds = [];
            AdmittedEnemySlots = [];
            UpdatedUtc = DateTime.UtcNow;
            return TowerResolutionResult.Defeat;
        }

        if (resolution.CombatDeathEnemySlots.Length != manifest.Enemies.Count) { return TowerResolutionResult.EncounterContinues; }

        LastClearedEncounterId = manifest.EncounterId;
        HighestFloorCleared = Math.Max(HighestFloorCleared, CurrentFloor);
        if (CurrentFloor % 5 == 0) { CheckpointFloor = CurrentFloor; }
        CurrentFloor = HighestFloorCleared == long.MaxValue ? long.MaxValue : HighestFloorCleared + 1;
        CurrentManifest = null;
        PendingFloorAdvance = CurrentFloor < long.MaxValue;

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
                PendingFloorAdvance = false;
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
        CurrentFloor = HighestFloorCleared == long.MaxValue ? long.MaxValue : HighestFloorCleared + 1;
        Status = TowerRunStatus.Active;
        EncounterRecoveryGeneration = 0;
        EncounterLifecycle = TowerEncounterLifecycle.NotStarted;
        AdmittedPlayerIds = [];
        AdmittedEnemySlots = [];
        RecoveryResourcesPending = false;
        PendingFloorAdvance = false;
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
    public long Floor { get; set; }
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
    public long Floor { get; set; }
    public int Tickets { get; set; }
    public long HighestFloorCleared { get; set; }
    public uint[] ItemTemplateIds { get; set; } = [];
}

public sealed class TowerEncounterManifest {
    public string EncounterId { get; set; } = string.Empty;
    public int AttemptGeneration { get; set; }
    public int EncounterRecoveryGeneration { get; set; }
    public long Floor { get; set; }
    public int PhysicalRoomNumber { get; set; }
    public ulong Seed { get; set; }
    public bool IsBoss { get; set; }
    public TowerChallengeMode ChallengeMode { get; set; } = TowerChallengeMode.Normal;
    public List<TowerEnemySpawn> Enemies { get; set; } = [];
}

public sealed class TowerEnemySpawn {
    public string EncounterId { get; set; } = string.Empty;
    public int RosterSlot { get; set; }
    public uint TemplateId { get; set; }
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

public sealed record TowerBossCatalogEntry(
    string World,
    uint TemplateId,
    string TemplatePath,
    string ObjectName,
    int BaseLevel,
    string SchoolId,
    int SelectionWeight);

public static class TowerContentCatalog {
    private const string CatalogResourceName = "Imlight.CoreLib.Game.Tower.tower-world-progression.json";
    private static readonly Lazy<CatalogDocument> s_catalog = new(LoadCatalog,
        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed class CatalogDocument {
        public int SchemaVersion { get; init; }
        public string SourceClientRevision { get; init; } = string.Empty;
        public WorldDefinition[] Worlds { get; init; } = [];
        public long EndlessRotationFirstFloor { get; init; }
        public int EndlessRotationBandSize { get; init; }
        public string[] EndlessRotationWorlds { get; init; } = [];
    }

    private sealed class WorldDefinition {
        public string Name { get; init; } = string.Empty;
        public string Folder { get; init; } = string.Empty;
        public long FirstFloor { get; init; }
        public int FloorCount { get; init; }
        public TowerBossCatalogEntry[] Bosses { get; init; } = [];
    }

    public static uint GetBossTemplateId(long floor) => GetBossSelection(floor, 0).TemplateId;
    public static uint GetBossTemplateId(long floor, ulong runSeed) => GetBossSelection(floor, runSeed).TemplateId;
    public static string GetBossWorld(long floor) => GetWorldDefinition(floor).Name;
    public static string GetBossTemplatePath(long floor) => GetBossSelection(floor, 0).TemplatePath;
    public static string GetBossTemplatePath(long floor, ulong runSeed) => GetBossSelection(floor, runSeed).TemplatePath;
    public static IReadOnlyList<TowerBossCatalogEntry> GetBossCandidates(long floor)
        => Array.AsReadOnly(GetWorldDefinition(floor).Bosses);

    public static TowerBossCatalogEntry GetBossSelection(long floor, ulong runSeed) {
        var world = GetWorldDefinition(floor);
        var totalWeight = world.Bosses.Aggregate(0L,
            (sum, boss) => checked(sum + boss.SelectionWeight));
        var selection = TowerSeed.Derive(runSeed, 0, floor, 0) % (ulong)totalWeight;
        foreach (var boss in world.Bosses) {
            if (selection < (ulong)boss.SelectionWeight) { return boss; }
            selection -= (ulong)boss.SelectionWeight;
        }

        throw new InvalidOperationException($"Tower boss weights for {world.Name} did not select a candidate.");
    }

    private static WorldDefinition GetWorldDefinition(long floor) {
        if (floor < 1) { throw new ArgumentOutOfRangeException(nameof(floor)); }
        var catalog = s_catalog.Value;
        if (floor >= catalog.EndlessRotationFirstFloor) {
            var band = (floor - catalog.EndlessRotationFirstFloor) / catalog.EndlessRotationBandSize;
            var index = (int)(band % catalog.EndlessRotationWorlds.Length);
            var worldName = catalog.EndlessRotationWorlds[index];
            return catalog.Worlds.Single(world => string.Equals(world.Name, worldName, StringComparison.Ordinal));
        }

        return catalog.Worlds.FirstOrDefault(world => floor >= world.FirstFloor
                && floor < world.FirstFloor + world.FloorCount)
            ?? throw new InvalidOperationException($"No Tower world band is configured for floor {floor}.");
    }

    private static CatalogDocument LoadCatalog() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidOperationException($"Missing embedded Tower world catalog {CatalogResourceName}.");
        var catalog = JsonSerializer.Deserialize<CatalogDocument>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Embedded Tower world catalog is empty.");
        ValidateCatalog(catalog);
        return catalog;
    }

    private static void ValidateCatalog(CatalogDocument catalog) {
        if (catalog.SchemaVersion != 1 || string.IsNullOrWhiteSpace(catalog.SourceClientRevision)
            || catalog.Worlds.Length == 0 || catalog.EndlessRotationBandSize <= 0) {
            throw new InvalidOperationException("Embedded Tower world catalog has invalid metadata.");
        }

        var worldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateIds = new HashSet<uint>();
        var expectedFirstFloor = 1L;
        foreach (var world in catalog.Worlds) {
            if (string.IsNullOrWhiteSpace(world.Name) || string.IsNullOrWhiteSpace(world.Folder)
                || world.FirstFloor != expectedFirstFloor || world.FloorCount <= 0 || world.Bosses.Length < 2
                || !worldNames.Add(world.Name)) {
                throw new InvalidOperationException($"Invalid Tower world progression band {world.Name}.");
            }

            foreach (var boss in world.Bosses) {
                if (!string.Equals(boss.World, world.Name, StringComparison.Ordinal)
                    || boss.TemplateId == 0 || string.IsNullOrWhiteSpace(boss.ObjectName)
                    || boss.BaseLevel < 1 || string.IsNullOrWhiteSpace(boss.SchoolId)
                    || !Enum.TryParse<MagicSchool>(boss.SchoolId, true, out var school)
                    || !Enum.IsDefined(school)
                    || boss.SelectionWeight <= 0
                    || string.IsNullOrWhiteSpace(boss.TemplatePath)
                    || !boss.TemplatePath.StartsWith($"ObjectData/{world.Folder}/", StringComparison.OrdinalIgnoreCase)
                    || !templateIds.Add(boss.TemplateId)) {
                    throw new InvalidOperationException($"Invalid native boss catalog entry {boss.TemplateId} in {world.Name}.");
                }
            }

            expectedFirstFloor = checked(expectedFirstFloor + world.FloorCount);
        }

        if (catalog.EndlessRotationFirstFloor != expectedFirstFloor
            || catalog.EndlessRotationWorlds.Length != catalog.Worlds.Length
            || catalog.EndlessRotationWorlds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Worlds.Length
            || catalog.EndlessRotationWorlds.Any(name => !worldNames.Contains(name))) {
            throw new InvalidOperationException("Embedded Tower endless world rotation is incomplete.");
        }
    }
}

internal static class TowerEncounterFactory {
    public static TowerEncounterManifest Create(string runId, ulong runSeed, int attemptGeneration,
                                                int encounterRecoveryGeneration, long floor, long highestFloorCleared,
                                                TowerChallengeMode challengeMode, int challengeReferenceLevel) {
        var seed = TowerSeed.Derive(runSeed, attemptGeneration, floor, encounterRecoveryGeneration);
        var boss = TowerContentCatalog.GetBossSelection(floor, runSeed);
        const bool isBoss = true;
        const int enemyCount = 1;
        var encounterId = $"{runId}/attempt-{attemptGeneration}/floor-{floor}/recovery-{encounterRecoveryGeneration}";
        var enemies = new List<TowerEnemySpawn>(enemyCount);

        for (var slot = 0; slot < enemyCount; slot++) {
            enemies.Add(new TowerEnemySpawn {
                EncounterId = encounterId,
                RosterSlot = slot,
                TemplateId = boss.TemplateId,
                ArchetypeId = $"native-boss/{boss.World}/{boss.TemplateId}",
                RoleId = "boss",
                SchoolId = boss.SchoolId,
                Level = TowerDifficultyModel.GetEffectiveLevel(floor, highestFloorCleared,
                    challengeMode, challengeReferenceLevel),
                HealthMultiplier = TowerDifficultyModel.GetHealthMultiplier(floor, challengeMode),
                SpellNames = [],
            });
        }

        return new TowerEncounterManifest {
            EncounterId = encounterId,
            AttemptGeneration = attemptGeneration,
            EncounterRecoveryGeneration = encounterRecoveryGeneration,
            Floor = floor,
            PhysicalRoomNumber = TowerZonePaths.GetPhysicalRoomNumber(floor),
            Seed = seed,
            IsBoss = isBoss,
            ChallengeMode = challengeMode,
            Enemies = enemies,
        };
    }
}

/// <summary>Bounded native-stat scaling and repeatable Ticket rewards for Tower floors.</summary>
public static class TowerDifficultyModel {
    private const int FallbackMaximumLevel = 170;

    /// <summary>Returns the loaded native cap, with the configured cap as a safe fallback.</summary>
    public static int MaximumLevel {
        get {
            try {
                _ = MagicLevelsConfig.Instance;
                if (MagicLevelsConfig.MaxLevel > 0) {
                    return Math.Clamp(MagicLevelsConfig.MaxLevel, 1, byte.MaxValue);
                }
            }
            catch {
                // Unit tests and early boot can run before Root resources are available.
            }

            try {
                return Math.Clamp(ConfigurationManager.GetValue("Character.MaxLevel", FallbackMaximumLevel), 1, byte.MaxValue);
            }
            catch {
                return FallbackMaximumLevel;
            }
        }
    }

    public static int GetEffectiveLevel(long floor, long highestFloorCleared,
        TowerChallengeMode mode, int challengeReferenceLevel) {
        if (floor < 1) { throw new ArgumentOutOfRangeException(nameof(floor)); }
        var maximumLevel = MaximumLevel;
        var tierLevel = Math.Min(floor, maximumLevel);
        var referenceLevel = Math.Clamp(challengeReferenceLevel, 1, maximumLevel);
        var useCurrentLevel = mode == TowerChallengeMode.ScaledChallenge || floor > highestFloorCleared;
        return (int)Math.Min(maximumLevel, useCurrentLevel ? Math.Max(tierLevel, referenceLevel) : tierLevel);
    }

    public static float GetHealthMultiplier(long floor, TowerChallengeMode mode) {
        if (floor < 1) { throw new ArgumentOutOfRangeException(nameof(floor)); }
        var floorGrowthRate = Math.Clamp(GetConfigValue("Tower.HealthGrowthPerSqrtFloor", 0.08d), 0d, 1d);
        var scaledHealthMultiplier = Math.Clamp(GetConfigValue("Tower.ScaledHealthMultiplier", 1.5d), 1d, 10d);
        var floorGrowth = floorGrowthRate * Math.Sqrt((double)floor - 1d);
        var modeMultiplier = mode == TowerChallengeMode.ScaledChallenge ? scaledHealthMultiplier : 1d;
        return (float)Math.Clamp((1d + floorGrowth) * modeMultiplier, 1d, 1_000_000d);
    }

    public static int GetVictoryTickets(long floor, TowerChallengeMode mode) {
        if (floor < 1) { throw new ArgumentOutOfRangeException(nameof(floor)); }
        var tierReward = 1d + Math.Floor(((double)floor - 1d) / 10d);
        var ticketBase = Math.Max(1, GetConfigValue("Tower.TicketsPerTenFloors", 1));
        var modeMultiplier = mode == TowerChallengeMode.ScaledChallenge
            ? Math.Max(1, GetConfigValue("Tower.ScaledTicketMultiplier", 2)) : 1;
        return (int)Math.Clamp(tierReward * ticketBase * modeMultiplier, 1d, int.MaxValue);
    }

    private static T GetConfigValue<T>(string key, T fallback) {
        try { return ConfigurationManager.GetValue(key, fallback); }
        catch { return fallback; }
    }
}

internal static class TowerProcessIncarnation {
    public static string Current { get; } = Guid.NewGuid().ToString("N");
}

internal static class TowerSeed {
    public static ulong Derive(ulong seed, int attempt, long floor, int recoveryGeneration) {
        var input = Encoding.UTF8.GetBytes($"{seed:x16}:{attempt}:{floor}:{recoveryGeneration}");
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
