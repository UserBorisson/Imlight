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

/// <summary>
/// Durable state for one Adventure run. The current manifest is materialized once and reused
/// until a floor transition; an attempt generation changes the manifest identity on restart.
/// </summary>
public sealed class TowerRunState {
    public const int FloorCount = 10;
    public const int PhysicalRoomCount = 5;
    public const int MaximumPartySize = 2;
    public const int MaximumEnemyCount = 2;

    public string RunId { get; set; } = string.Empty;
    public ulong OwnerWizardId { get; set; }
    public ulong RunSeed { get; set; }
    public int CurrentFloor { get; set; } = 1;
    public int HighestFloorCleared { get; set; }
    public int CheckpointFloor { get; set; }
    public int AttemptGeneration { get; set; } = 1;
    public long AuthorityEpoch { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public TowerRunStatus Status { get; set; } = TowerRunStatus.Active;
    public TowerEncounterManifest CurrentManifest { get; set; }
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
        AuthorityEpoch = AuthorityEpoch,
        Revision = Revision,
        Status = Status,
        CurrentManifest = CurrentManifest is null ? null : new TowerEncounterManifest {
            EncounterId = CurrentManifest.EncounterId,
            AttemptGeneration = CurrentManifest.AttemptGeneration,
            Floor = CurrentManifest.Floor,
            PhysicalRoomNumber = CurrentManifest.PhysicalRoomNumber,
            Seed = CurrentManifest.Seed,
            IsBoss = CurrentManifest.IsBoss,
            Enemies = [.. CurrentManifest.Enemies.Select(enemy => new TowerEnemySpawn {
                ArchetypeId = enemy.ArchetypeId,
                RoleId = enemy.RoleId,
                SchoolId = enemy.SchoolId,
                Level = enemy.Level,
                HealthMultiplier = enemy.HealthMultiplier,
                SpellNames = [.. enemy.SpellNames],
            })],
        },
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

    public static TowerRunState Create(string runId, ulong ownerWizardId, ulong runSeed) {
        if (string.IsNullOrWhiteSpace(runId)) { throw new ArgumentException("A run id is required.", nameof(runId)); }
        if (ownerWizardId == 0) { throw new ArgumentOutOfRangeException(nameof(ownerWizardId)); }

        return new TowerRunState {
            RunId = runId,
            OwnerWizardId = ownerWizardId,
            RunSeed = runSeed,
        };
    }

    public TowerEncounterManifest BuildCurrentManifest() {
        if (CurrentManifest is not null) { return CurrentManifest; }
        if (Status != TowerRunStatus.Active || CurrentFloor is < 1 or > FloorCount) {
            throw new InvalidOperationException("A manifest can only be created for an active floor.");
        }

        CurrentManifest = TowerEncounterFactory.Create(RunId, RunSeed, AttemptGeneration, CurrentFloor);
        UpdatedUtc = DateTime.UtcNow;
        return CurrentManifest;
    }

    public TowerResolutionResult ApplyResolution(TowerEncounterResolution resolution) {
        ArgumentNullException.ThrowIfNull(resolution);

        if (Status == TowerRunStatus.Completed) { return TowerResolutionResult.RunCompleted; }
        if (Status != TowerRunStatus.Active) { return TowerResolutionResult.RunNotActive; }
        if (resolution.AuthorityEpoch != AuthorityEpoch) { return TowerResolutionResult.StaleAuthority; }

        var manifest = BuildCurrentManifest();
        if (!string.Equals(resolution.EncounterId, manifest.EncounterId, StringComparison.Ordinal)) {
            return TowerResolutionResult.NotCurrentEncounter;
        }

        if (resolution.PlayerCount is < 1 or > MaximumPartySize
            || resolution.AlivePlayerCount < 0
            || resolution.AlivePlayerCount > resolution.PlayerCount
            || resolution.EnemyCount is < 1 or > MaximumEnemyCount
            || resolution.EnemyCount != manifest.Enemies.Count
            || resolution.AliveEnemyCount < 0
            || resolution.AliveEnemyCount > resolution.EnemyCount) {
            return TowerResolutionResult.InvalidSnapshot;
        }

        // Player loss wins ties. A simultaneous final hit is a defeat and earns no clear/reward.
        if (resolution.AlivePlayerCount == 0) {
            Status = TowerRunStatus.Defeated;
            UpdatedUtc = DateTime.UtcNow;
            return TowerResolutionResult.Defeat;
        }

        if (resolution.AliveEnemyCount > 0) { return TowerResolutionResult.EncounterContinues; }

        HighestFloorCleared = Math.Max(HighestFloorCleared, CurrentFloor);
        if (CurrentFloor is 5 or 10) { CheckpointFloor = CurrentFloor; }

        if (CurrentFloor == FloorCount) {
            Status = TowerRunStatus.Completed;
        }
        else {
            CurrentFloor++;
            CurrentManifest = null;
        }

        UpdatedUtc = DateTime.UtcNow;
        return TowerResolutionResult.Victory;
    }

    /// <summary>Increments the fencing token when a recovered actor becomes the sole writer.</summary>
    public long ReacquireAuthority() {
        checked { AuthorityEpoch++; }
        UpdatedUtc = DateTime.UtcNow;
        return AuthorityEpoch;
    }

    /// <summary>
    /// Starts a new attempt after defeat or extraction, resuming immediately after the last
    /// completed checkpoint (or floor 1 when no checkpoint has been reached).
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
        CurrentFloor = Math.Min(CheckpointFloor + 1, FloorCount);
        Status = TowerRunStatus.Active;
        CurrentManifest = null;
        UpdatedUtc = DateTime.UtcNow;
    }
}

public sealed class TowerEncounterResolution {
    public long AuthorityEpoch { get; set; }
    public string EncounterId { get; set; } = string.Empty;
    public int PlayerCount { get; set; }
    public int AlivePlayerCount { get; set; }
    public int EnemyCount { get; set; }
    public int AliveEnemyCount { get; set; }
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
    public int Floor { get; set; }
    public int PhysicalRoomNumber { get; set; }
    public ulong Seed { get; set; }
    public bool IsBoss { get; set; }
    public List<TowerEnemySpawn> Enemies { get; set; } = [];
}

public sealed class TowerEnemySpawn {
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
    public static TowerEncounterManifest Create(string runId, ulong runSeed, int attemptGeneration, int floor) {
        var seed = TowerSeed.Derive(runSeed, attemptGeneration, floor);
        var random = new TowerRandom(seed);
        var isBoss = floor is 5 or 10;
        var enemyCount = floor == 10 ? 2 : isBoss ? 1 : random.Next(1, TowerRunState.MaximumEnemyCount + 1);
        var enemies = new List<TowerEnemySpawn>(enemyCount);

        for (var slot = 0; slot < enemyCount; slot++) {
            var archetype = floor switch {
                5 => TowerContentCatalog.FloorFiveBoss,
                10 when slot == 0 => TowerContentCatalog.FloorTenBoss,
                10 => TowerContentCatalog.RegularArchetypes[random.Next(0, TowerContentCatalog.RegularArchetypes.Count)],
                _ => TowerContentCatalog.RegularArchetypes[random.Next(0, TowerContentCatalog.RegularArchetypes.Count)],
            };

            enemies.Add(new TowerEnemySpawn {
                ArchetypeId = archetype.Id,
                RoleId = archetype.RoleId,
                SchoolId = archetype.SchoolId,
                Level = archetype.BaseLevel + Math.Max(0, floor - 1),
                HealthMultiplier = archetype.HealthMultiplier + ((floor - 1) * 0.025f),
                SpellNames = [.. archetype.SpellNames],
            });
        }

        return new TowerEncounterManifest {
            EncounterId = $"{runId}/attempt-{attemptGeneration}/floor-{floor}",
            AttemptGeneration = attemptGeneration,
            Floor = floor,
            PhysicalRoomNumber = ((floor - 1) % TowerRunState.PhysicalRoomCount) + 1,
            Seed = seed,
            IsBoss = isBoss,
            Enemies = enemies,
        };
    }
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
