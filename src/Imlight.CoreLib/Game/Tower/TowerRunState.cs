using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Imlight.Common;
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
    public const int CombatRoomCount = PhysicalRoomCount - 1;
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
        if (CurrentFloor % 5 == 0) { CheckpointFloor = CurrentFloor; }
        CurrentFloor = HighestFloorCleared == long.MaxValue ? long.MaxValue : HighestFloorCleared + 1;
        CurrentManifest = null;

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
        CurrentFloor = HighestFloorCleared == long.MaxValue ? long.MaxValue : HighestFloorCleared + 1;
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

public static class TowerContentCatalog {
    private sealed record NativeBossDefinition(string World, uint TemplateId, string TemplatePath);

    private static readonly NativeBossDefinition[] s_worldBosses = [
        new("Wizard City", 35099, "ObjectData/WC/Wraith-WCBoss-L05.xml"),
        new("Krokotopia", 35325, "ObjectData/KT/KrokMummy-Fire-KTBoss-L03.xml"),
        new("Marleybone", 39848, "ObjectData/MB/Cat-Meowiarty-MBBoss-L61.xml"),
        new("MooShu", 77457, "ObjectData/MS/Oni-Jade-MSBoss-L76.xml"),
        new("Dragonspyre", 126504, "ObjectData/DS/Malistaire-Boss-R10.xml"),
        new("Celestia", 267110, "ObjectData/CL/CL-CrabKing-Boss-R10.xml"),
        new("Zafaria", 469944, "ObjectData/ZF/ZF-Belloq-R10-Boss.xml"),
        new("Avalon", 589403, "ObjectData/AV/AV-Wyrm-Jabberwock-R11-01.xml"),
        new("Azteca", 684530, "ObjectData/AZ/AZ-Elephant-Belloq-A-Script.xml"),
        new("Khrysalis", 1283364, "ObjectData/KR/KR_MorgantheBlue_A_Bossfight.xml"),
        new("Polaris", 1340541, "ObjectData/PL/PL_BabaYaga_A.xml"),
        new("Mirage", 1384772, "ObjectData/MR/MR_Shadow_Xerxes_A_Boss_01.xml"),
        new("Empyrea", 1430018, "ObjectData/EM/EM_Brain_Master_A_Boss_01.xml"),
        new("Karamelle", 1505840, "ObjectData/KM/KM_Nothing_Divine_A_01.xml"),
        new("Lemuria", 1530858, "ObjectData/LM/LM_Khrulhu_Scion_A_01.xml"),
        new("Novus", 1569503, "ObjectData/NV/NV_Penguin_MaxineF_A_01.xml"),
        new("Wallaru", 1640552, "ObjectData/WL/WL_Nightmare_Malus_A_01.xml"),
    ];

    public static uint GetBossTemplateId(long floor) => GetBossDefinition(floor).TemplateId;
    public static string GetBossWorld(long floor) => GetBossDefinition(floor).World;
    public static string GetBossTemplatePath(long floor) => GetBossDefinition(floor).TemplatePath;

    private static NativeBossDefinition GetBossDefinition(long floor) {
        if (floor < 1) { throw new ArgumentOutOfRangeException(nameof(floor)); }

        var worldIndex = floor switch {
            <= 14 => 0,
            <= 24 => 1,
            <= 34 => 2,
            _ => 3 + (int)((floor - 35) / 10),
        };

        // Floors 35 through 164 follow the canonical world order. From floor 165 onward,
        // the same native boss-rank catalog rotates in ten-floor bands without a terminal floor.
        if (floor >= 165) {
            worldIndex = 16 + (int)(((floor - 165) / 10) % s_worldBosses.Length);
            worldIndex %= s_worldBosses.Length;
        }
        else {
            worldIndex = Math.Min(worldIndex, s_worldBosses.Length - 1);
        }

        return s_worldBosses[worldIndex];
    }

}

internal static class TowerEncounterFactory {
    public static TowerEncounterManifest Create(string runId, ulong runSeed, int attemptGeneration,
                                                int encounterRecoveryGeneration, long floor, long highestFloorCleared,
                                                TowerChallengeMode challengeMode, int challengeReferenceLevel) {
        var seed = TowerSeed.Derive(runSeed, attemptGeneration, floor, encounterRecoveryGeneration);
        const bool isBoss = true;
        const int enemyCount = 1;
        var encounterId = $"{runId}/attempt-{attemptGeneration}/floor-{floor}/recovery-{encounterRecoveryGeneration}";
        var enemies = new List<TowerEnemySpawn>(enemyCount);

        for (var slot = 0; slot < enemyCount; slot++) {
            enemies.Add(new TowerEnemySpawn {
                EncounterId = encounterId,
                RosterSlot = slot,
                TemplateId = TowerContentCatalog.GetBossTemplateId(floor),
                ArchetypeId = "native-boss/" + TowerContentCatalog.GetBossWorld(floor),
                RoleId = "boss",
                SchoolId = string.Empty,
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
