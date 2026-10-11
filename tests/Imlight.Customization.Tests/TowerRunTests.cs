using System.Reflection;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Game.Tower;

internal static class TowerRunTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        RunStateAndManifestContract();
        VictoryAndCheckpointProgression();
        DefeatAndMutualDeathAreLosses();
        AuthorityEpochFencesLateResults();
        InvalidSnapshotsDoNotAdvance();
        CanonicalRoomPathsCycleAcrossTenFloors();
        Console.WriteLine("PASS: tower run state, deterministic manifests, checkpoints, defeats and authority fencing");
    }

    private static void RunStateAndManifestContract() {
        var first = NewRun("manifest-contract", 991827);
        var second = NewRun("manifest-contract", 991827);
        Equal(1, IntProperty(first, "CurrentFloor"), "new run starts on floor 1");
        Equal(0, IntProperty(first, "HighestFloorCleared"), "new run has no cleared floors");
        Equal(0, IntProperty(first, "CheckpointFloor"), "new run has no extraction checkpoint");
        Equal(1L, LongProperty(first, "AuthorityEpoch"), "new run starts with authority epoch 1");

        var firstManifest = BuildManifest(first);
        var secondManifest = BuildManifest(second);
        Equal(1, IntProperty(firstManifest, "Floor"), "first manifest floor");
        Equal(1, IntProperty(firstManifest, "PhysicalRoomNumber"), "logical floor 1 uses physical room 1");
        Equal(ManifestSignature(firstManifest), ManifestSignature(secondManifest), "same run seed yields same manifest");
        Equal(1, CollectionCount(firstManifest, "Enemies"), "manifest is bounded to one or two enemies");

        for (var floor = 1; floor <= 10; floor++) {
            var state = NewRun("room-map-" + floor, 1000 + (ulong) floor);
            SetInt(state, "CurrentFloor", floor);
            var manifest = BuildManifest(state);
            Equal(((floor - 1) % 5) + 1, IntProperty(manifest, "PhysicalRoomNumber"), $"physical room for floor {floor}");
            Equal(floor is 5 or 10, BoolProperty(manifest, "IsBoss"), $"boss flag for floor {floor}");
            var enemyCount = CollectionCount(manifest, "Enemies");
            Check(enemyCount is 1 or 2, $"floor {floor} must contain one or two enemies, got {enemyCount}");
        }
    }

    private static void VictoryAndCheckpointProgression() {
        var state = NewRun("progression", 44);
        for (var floor = 1; floor <= 10; floor++) {
            var manifest = BuildManifest(state);
            Equal(floor, IntProperty(manifest, "Floor"), "manifest follows current floor");
            var result = Apply(state, manifest, CollectionCount(manifest, "Enemies"), alivePlayers: 1, aliveEnemies: 0);
            Equal("Victory", result, $"floor {floor} clean victory");
            Equal(floor, IntProperty(state, "HighestFloorCleared"), $"highest cleared after floor {floor}");

            if (floor == 5) {
                Equal(5, IntProperty(state, "CheckpointFloor"), "floor 5 is an extraction checkpoint");
                Equal(6, IntProperty(state, "CurrentFloor"), "floor 6 follows the first checkpoint");
            }
        }

        Equal("Completed", EnumProperty(state, "Status"), "floor 10 completes the run");
        Equal(10, IntProperty(state, "HighestFloorCleared"), "floor 10 is recorded");
        Equal(10, IntProperty(state, "CheckpointFloor"), "floor 10 is a checkpoint");
        Equal("RunCompleted", Apply(state, BuildManifest(state), 1, 1, 0), "completed runs reject further resolutions");

        var checkpointRun = NewRun("checkpoint-restart", 77);
        for (var floor = 1; floor <= 5; floor++) {
            var manifest = BuildManifest(checkpointRun);
            Equal("Victory", Apply(checkpointRun, manifest, CollectionCount(manifest, "Enemies"), 1, 0), "checkpoint setup win");
        }
        var oldAttempt = IntProperty(checkpointRun, "AttemptGeneration");
        Invoke(checkpointRun, "RestartFromCheckpoint");
        Equal(6, IntProperty(checkpointRun, "CurrentFloor"), "restart returns to the floor after checkpoint");
        Equal(oldAttempt + 1, IntProperty(checkpointRun, "AttemptGeneration"), "restart creates a new attempt generation");
    }

    private static void DefeatAndMutualDeathAreLosses() {
        var ordinaryDefeat = NewRun("ordinary-defeat", 1);
        var manifest = BuildManifest(ordinaryDefeat);
        Equal("Defeat", Apply(ordinaryDefeat, manifest, CollectionCount(manifest, "Enemies"), 0, 1), "party death is defeat");
        Equal(0, IntProperty(ordinaryDefeat, "HighestFloorCleared"), "defeat does not clear the floor");
        Equal("Defeated", EnumProperty(ordinaryDefeat, "Status"), "defeat is persisted as run status");

        var mutualDeath = NewRun("mutual-death", 2);
        manifest = BuildManifest(mutualDeath);
        Equal("Defeat", Apply(mutualDeath, manifest, CollectionCount(manifest, "Enemies"), 0, 0), "mutual death resolves as defeat");
        Equal(0, IntProperty(mutualDeath, "HighestFloorCleared"), "mutual death cannot grant floor progress");
    }

    private static void AuthorityEpochFencesLateResults() {
        var state = NewRun("authority", 9);
        var oldManifest = BuildManifest(state);
        var oldEpoch = LongProperty(state, "AuthorityEpoch");
        var newEpoch = (long) Invoke(state, "ReacquireAuthority");
        Equal(oldEpoch + 1, newEpoch, "recovery fences the previous owner");
        Equal("StaleAuthority", Apply(state, oldManifest, CollectionCount(oldManifest, "Enemies"), 1, 0, oldEpoch), "old owner cannot advance the run");
        Equal(1, IntProperty(state, "CurrentFloor"), "stale result leaves floor unchanged");
        Equal("Victory", Apply(state, BuildManifest(state), CollectionCount(oldManifest, "Enemies"), 1, 0, newEpoch), "current owner can resolve the encounter");
    }

    private static void InvalidSnapshotsDoNotAdvance() {
        var state = NewRun("invalid-snapshot", 13);
        var manifest = BuildManifest(state);
        Equal("InvalidSnapshot", Apply(state, manifest, 3, 1, 0), "three enemies violate the encounter cap");
        Equal("InvalidSnapshot", Apply(state, manifest, 0, 1, 0), "zero enemies cannot resolve an encounter");
        Equal("InvalidSnapshot", Apply(state, manifest, CollectionCount(manifest, "Enemies"), 3, 0), "three player slots violate the party cap");
        Equal(1, IntProperty(state, "CurrentFloor"), "invalid snapshots leave floor unchanged");
    }

    private static void CanonicalRoomPathsCycleAcrossTenFloors() {
        Equal("WizardCity/WC_Golem_Tower", TowerZonePaths.EntryZone, "existing Golem Court entry path");
        for (var floor = 1; floor <= 10; floor++) {
            var expectedRoom = ((floor - 1) % 5) + 1;
            var path = TowerZonePaths.GetRoomPath(floor);
            Check(TowerZonePaths.TryGetRoomNumber(path, out var actualRoom), $"floor {floor} path is recognized as a tower room");
            Equal(expectedRoom, actualRoom, $"floor {floor} maps to its reusable physical room");
        }
    }

    private static object NewRun(string runId, ulong seed) {
        var type = RequireType("Imlight.CoreLib.Game.Tower.TowerRunState");
        return type.GetMethod("Create", [typeof(string), typeof(ulong), typeof(ulong)])
            ?.Invoke(null, [runId, 701UL, seed])
            ?? throw new InvalidOperationException("TowerRunState.Create(string, ulong, ulong) was not found");
    }

    private static object BuildManifest(object state)
        => Invoke(state, "BuildCurrentManifest");

    private static string ManifestSignature(object manifest) {
        var enemies = GetProperty(manifest, "Enemies") as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("manifest Enemies is not enumerable");
        var enemySignatures = new List<string>();
        foreach (var enemy in enemies) {
            enemySignatures.Add($"{StringProperty(enemy, "ArchetypeId")}:{StringProperty(enemy, "RoleId")}");
        }
        return $"{IntProperty(manifest, "Floor")}:{IntProperty(manifest, "PhysicalRoomNumber")}:{BoolProperty(manifest, "IsBoss")}:{string.Join(',', enemySignatures)}";
    }

    private static string Apply(object state, object manifest, int enemyCount, int alivePlayers, int aliveEnemies, long? epoch = null) {
        var snapshotType = RequireType("Imlight.CoreLib.Game.Tower.TowerEncounterResolution");
        var snapshot = Activator.CreateInstance(snapshotType) ?? throw new InvalidOperationException("Could not create resolution snapshot");
        Set(snapshot, "AuthorityEpoch", epoch ?? LongProperty(state, "AuthorityEpoch"));
        Set(snapshot, "EncounterId", StringProperty(manifest, "EncounterId"));
        Set(snapshot, "PlayerCount", 1);
        Set(snapshot, "AlivePlayerCount", alivePlayers);
        Set(snapshot, "EnemyCount", enemyCount);
        Set(snapshot, "AliveEnemyCount", aliveEnemies);
        return Invoke(state, "ApplyResolution", snapshot).ToString();
    }

    private static Type RequireType(string fullName)
        => CoreAssembly.GetType(fullName) ?? throw new InvalidOperationException($"Missing tower contract type {fullName}");

    private static object Invoke(object instance, string name, params object[] args) {
        var method = instance.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing public method {instance.GetType().Name}.{name}");
        var result = method.Invoke(instance, args);
        return result ?? (method.ReturnType == typeof(void) ? new object() : throw new InvalidOperationException($"{name} returned null"));
    }

    private static object GetProperty(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Missing public property {instance.GetType().Name}.{name}");

    private static int IntProperty(object instance, string name) => Convert.ToInt32(GetProperty(instance, name));
    private static long LongProperty(object instance, string name) => Convert.ToInt64(GetProperty(instance, name));
    private static bool BoolProperty(object instance, string name) => Convert.ToBoolean(GetProperty(instance, name));
    private static string StringProperty(object instance, string name) => Convert.ToString(GetProperty(instance, name));
    private static string EnumProperty(object instance, string name) => GetProperty(instance, name).ToString();

    private static int CollectionCount(object instance, string name)
        => (GetProperty(instance, name) as System.Collections.ICollection)?.Count
            ?? throw new InvalidOperationException($"{name} is not an ICollection");

    private static void SetInt(object instance, string name, int value) => Set(instance, name, value);
    private static void Set(object instance, string name, object value) {
        var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing public property {instance.GetType().Name}.{name}");
        property.SetValue(instance, value);
    }

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
