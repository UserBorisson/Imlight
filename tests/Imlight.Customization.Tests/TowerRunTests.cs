using System.Reflection;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Behaviors;

internal static class TowerRunTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        TowerWizardStatsTests.Run();
        TowerWizardDeckTests.Run();
        InfiniteOneBossContract();
        CanonicalNativeBossWorldCatalog();
        FloorSelectionAndChallengeScaling();
        RunStateAndManifestContract();
        VictoryAndCheckpointProgression();
        RestartResumesAtNextUnclearedFloor();
        RecoveryAllowanceIsRunWideAndCombatLifecycleIsExplicit();
        DefeatAndMutualDeathAreLosses();
        AuthorityEpochFencesLateResults();
        InvalidSnapshotsDoNotAdvance();
        StandardArenaRoomIsReusedAcrossFortyFloors();
        FloorClearLeavesTheRunReadyForAnUpstairsAdvance();
        FloorClearFeedbackReflectsCommittedProgress();
        Console.WriteLine("PASS: tower run state, deterministic manifests, checkpoints, defeats and authority fencing");
    }

    private static void FloorSelectionAndChallengeScaling() {
        var state = NewRun("selection-and-scaling", 901);
        Check(!(bool)Invoke(state, "TrySelectFloor", 2L, 50), "locked future floors cannot be selected");
        Check((bool)Invoke(state, "TrySetChallengeMode", TowerChallengeMode.Normal, 50),
            "a wizard can configure Normal mode in the sanctuary");
        var manifest = BuildManifest(state);
        var enemy = ((System.Collections.IEnumerable)GetProperty(manifest, "Enemies")).Cast<object>().Single();
        Equal(1, IntProperty(enemy, "Level"), "Normal floor 1 retains its progression tier for an existing level-50 wizard");
        foreach (var floor in new long[] { 1, 20, 80, 150 }) {
            foreach (var wizardLevel in new[] { 1, 50, 170 }) {
                Equal((int)Math.Min(floor, TowerDifficultyModel.MaximumLevel),
                    TowerDifficultyModel.GetEffectiveLevel(floor, floor - 1,
                        TowerChallengeMode.Normal, wizardLevel),
                    $"Normal floor {floor} is independent of wizard level {wizardLevel}");
            }
        }
        Check(!(bool)Invoke(state, "TrySelectFloor", 1L, 50), "floor selection is frozen after the manifest is materialized");

        var returnedBeforeCombat = NewRun("returned-before-combat", 903);
        SetInt(returnedBeforeCombat, "HighestFloorCleared", 2L);
        SetInt(returnedBeforeCombat, "CurrentFloor", 3L);
        _ = BuildManifest(returnedBeforeCombat);
        Check((bool)Invoke(returnedBeforeCombat, "CaptureFloorEntryResource", 701UL, 80, 40),
            "the unstarted encounter fixture captures its entry resources");
        Check((bool)Invoke(returnedBeforeCombat, "TryDiscardUnstartedEncounter"),
            "returning to the sanctuary retires a materialized encounter that never entered combat");
        Check(returnedBeforeCombat.GetType().GetProperty("CurrentManifest")?.GetValue(returnedBeforeCombat) is null,
            "discarding an unstarted encounter allows the Tome to reconfigure it");
        Equal(0, CollectionCount(returnedBeforeCombat, "FloorEntryResources"),
            "discarding an unstarted encounter also retires its unused entry snapshot");
        Check((bool)Invoke(returnedBeforeCombat, "TrySetChallengeMode", TowerChallengeMode.ScaledChallenge, 50),
            "challenge mode can be changed after returning from an unstarted encounter");
        Check((bool)Invoke(returnedBeforeCombat, "TrySelectFloor", 1L, 50),
            "a cleared floor can be selected after an unstarted encounter is retired");
        Equal(1, IntProperty(returnedBeforeCombat, "CurrentFloor"),
            "Tome selection updates the same authoritative run after the return trip");

        var activeEncounter = NewRun("active-encounter-not-discarded", 904);
        var activeManifest = BuildManifest(activeEncounter);
        Check((bool)Invoke(activeEncounter, "TryBeginEncounter", new ulong[] { 904 }, new int[] { 0 }),
            "the Tome regression fixture enters combat");
        Check(!(bool)Invoke(activeEncounter, "TryDiscardUnstartedEncounter"),
            "returning to the sanctuary cannot discard an admitted encounter");
        Equal(StringProperty(activeManifest, "EncounterId"),
            StringProperty(GetProperty(activeEncounter, "CurrentManifest"), "EncounterId"),
            "an active encounter retains its original manifest identity");

        var clearedRun = NewRun("cleared-floor-selection", 902);
        SetInt(clearedRun, "HighestFloorCleared", 5L);
        SetInt(clearedRun, "CurrentFloor", 6L);
        Check((bool)Invoke(clearedRun, "TrySelectFloor", 2L, 50), "any cleared floor is selectable in the sanctuary");
        Check((bool)Invoke(clearedRun, "TrySetChallengeMode", TowerChallengeMode.ScaledChallenge, 50),
            "Scaled Challenge can be selected before entry");
        manifest = BuildManifest(clearedRun);
        enemy = ((System.Collections.IEnumerable)GetProperty(manifest, "Enemies")).Cast<object>().Single();
        Equal(50, IntProperty(enemy, "Level"), "Scaled Challenge adapts a replay to the current wizard level");
        Equal(1.5f, Convert.ToSingle(GetProperty(enemy, "HealthMultiplier")),
            "Scaled Challenge has an explicit health modifier");
        Equal(1f, TowerDifficultyModel.GetHealthMultiplier(100, TowerChallengeMode.Normal),
            "leveling floors use player-like health plus simulated equipment without monster health inflation");
        Check(TowerDifficultyModel.GetHealthMultiplier(TowerDifficultyModel.MaximumLevel + 100L,
            TowerChallengeMode.Normal) > 1f, "post-cap floors grow beyond the capped wizard equipment profile");
        Equal(4, TowerDifficultyModel.GetVictoryTickets(20, TowerChallengeMode.ScaledChallenge),
            "ticket rewards scale by tier and challenge mode");
        Equal(int.MaxValue, TowerDifficultyModel.GetVictoryTickets(long.MaxValue, TowerChallengeMode.ScaledChallenge),
            "endless ticket arithmetic saturates instead of overflowing");
        Equal(TowerDifficultyModel.MaximumLevel,
            TowerDifficultyModel.GetEffectiveLevel(long.MaxValue, long.MaxValue - 1,
                TowerChallengeMode.Normal, int.MaxValue),
            "endless floor scaling respects the server's native level cap");
        Equal("Victory", Apply(clearedRun, manifest, 1, 1, 0), "replaying a cleared floor remains playable");
        Equal(6L, LongProperty(clearedRun, "CurrentFloor"), "replaying an old floor leaves the next progression floor selected");
    }

    private static void InfiniteOneBossContract() {
        foreach (var floor in new[] { 1L, 5L, 10L, 11L, 100_000L, long.MaxValue }) {
            var state = NewRun($"infinite-boss-{floor}", (ulong)floor + 9000);
            SetInt(state, "CurrentFloor", floor);
            var manifest = BuildManifest(state);
            Equal(1, CollectionCount(manifest, "Enemies"), $"floor {floor} contains exactly one enemy");
            Check(BoolProperty(manifest, "IsBoss"), $"floor {floor} is a boss encounter");
            var enemy = ((System.Collections.IEnumerable)GetProperty(manifest, "Enemies")).Cast<object>().First();
            Equal("boss", StringProperty(enemy, "RoleId"), $"floor {floor} uses the boss AI profile");
            Check(UIntProperty(enemy, "TemplateId") != 0, $"floor {floor} selects a native boss template");
            Equal(2, IntProperty(manifest, "PhysicalRoomNumber"),
                $"floor {floor} uses the single standardized combat arena");
        }
    }

    private static void CanonicalNativeBossWorldCatalog() {
        uint[] nativeWizardIds = [126730, 126709, 126736, 146411, 126743, 126731, 126741,
            126734, 136177, 126732, 136180, 126737, 126795];
        var cases = new (long Floor, string World)[] {
            (1, "Wizard City"), (14, "Wizard City"),
            (15, "Krokotopia"), (24, "Krokotopia"),
            (25, "Marleybone"), (34, "Marleybone"),
            (35, "MooShu"), (45, "Dragonspyre"),
            (55, "Celestia"), (65, "Zafaria"),
            (75, "Avalon"), (85, "Azteca"),
            (95, "Khrysalis"), (105, "Polaris"),
            (115, "Mirage"), (125, "Empyrea"),
            (135, "Karamelle"), (145, "Lemuria"),
            (155, "Novus"), (165, "Wallaru"),
            (174, "Wallaru"), (175, "Wizard City"),
            (185, "Krokotopia"), (335, "Wallaru"),
        };
        foreach (var (floor, world) in cases) {
            Equal(world, TowerContentCatalog.GetBossWorld(floor), $"floor {floor} world");
            var candidates = TowerContentCatalog.GetBossCandidates(floor);
            Equal(nativeWizardIds.Length, candidates.Count, $"floor {floor} uses the curated human wizard pool");
            Check(candidates.Select(candidate => candidate.TemplateId).ToHashSet().SetEquals(nativeWizardIds),
                $"floor {floor} selects only audited native wizard NPCs");
            Equal(7, candidates.Select(candidate => candidate.SchoolId).Distinct().Count(),
                $"floor {floor} retains all seven authentic wizard schools");

            var seenTemplateIds = new HashSet<uint>();
            foreach (var seed in Enumerable.Range(1, 64).Select(value => (ulong)value)) {
                var boss = TowerContentCatalog.GetBossSelection(floor, seed);
                Equal(world, boss.World, $"floor {floor} seeded boss world");
                Check(boss.TemplateId != 0, $"floor {floor} seeded boss has a native template ID");
                Check(boss.TemplatePath.StartsWith("ObjectData/DS/", StringComparison.OrdinalIgnoreCase)
                    && (boss.ObjectName.StartsWith("Wizard-", StringComparison.Ordinal)
                        || boss.ObjectName == "Student-Boss-R8"),
                    $"floor {floor} preserves the audited native human wizard asset");
                Equal("Dragonspyre", StringProperty(boss, "NativeWorld"),
                    $"floor {floor} keeps native provenance separate from the progression world band");
                Check(boss.BaseLevel is >= 1 and <= 24, $"floor {floor} boss uses a supported native level profile");
                Check(Enum.TryParse<MagicSchool>(boss.SchoolId, true, out _),
                    $"floor {floor} boss uses a supported native school");

                var repeatedBoss = TowerContentCatalog.GetBossSelection(floor, seed);
                Equal(boss.TemplateId, repeatedBoss.TemplateId,
                    $"floor {floor} uses a stable boss for a run seed");
                Equal(boss.TemplatePath, repeatedBoss.TemplatePath,
                    $"floor {floor} uses a stable asset path for a run seed");
                seenTemplateIds.Add(boss.TemplateId);
            }

            Equal(1, seenTemplateIds.Count, $"floor {floor} has the same curated opponent for every run");
        }
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
        Equal(2, IntProperty(firstManifest, "PhysicalRoomNumber"), "logical floor 1 uses physical room 2");
        Equal(ManifestSignature(firstManifest), ManifestSignature(secondManifest), "same run seed yields same manifest");
        Equal(1, CollectionCount(firstManifest, "Enemies"), "manifest contains exactly one boss");

        for (var floor = 1; floor <= 40; floor++) {
            var state = NewRun("room-map-" + floor, 1000 + (ulong) floor);
            SetInt(state, "CurrentFloor", floor);
            var manifest = BuildManifest(state);
            Equal(2, IntProperty(manifest, "PhysicalRoomNumber"), $"standardized physical arena for floor {floor}");
            Check(BoolProperty(manifest, "IsBoss"), $"floor {floor} is a boss encounter");
            var enemyCount = CollectionCount(manifest, "Enemies");
            Equal(1, enemyCount, $"floor {floor} has exactly one enemy");
        }
    }

    private static void VictoryAndCheckpointProgression() {
        var state = NewRun("progression", 44);
        for (var floor = 1; floor <= 25; floor++) {
            var manifest = BuildManifest(state);
            Equal(floor, IntProperty(manifest, "Floor"), "manifest follows current floor");
            var result = Apply(state, manifest, CollectionCount(manifest, "Enemies"), alivePlayers: 1, aliveEnemies: 0);
            Equal("Victory", result, $"floor {floor} clean victory");
            Equal(floor, IntProperty(state, "HighestFloorCleared"), $"highest cleared after floor {floor}");
            Equal(1, IntProperty(state, "RecoveryAllowanceRemaining"), $"floor {floor} victory grants one recovery for the next floor");
            Equal(0, CollectionCount(state, "FloorEntryResources"), $"floor {floor} victory clears its entry resource snapshot");

            if (floor == 5) {
                Equal(5, IntProperty(state, "CheckpointFloor"), "floor 5 is an extraction checkpoint");
                Equal(6, IntProperty(state, "CurrentFloor"), "floor 6 follows the first checkpoint");
            }
        }

        Equal("Active", EnumProperty(state, "Status"), "the tower remains active past floor 10");
        Equal(25, IntProperty(state, "HighestFloorCleared"), "floor 25 is recorded");
        Equal(26, IntProperty(state, "CurrentFloor"), "floor 26 follows floor 25 without a terminal floor");
        Equal("Victory", Apply(state, BuildManifest(state), 1, 1, 0), "floor 26 remains playable");

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

    private static void RestartResumesAtNextUnclearedFloor() {
        foreach (var highestClear in new[] { 0, 1, 3, 5, 8, 9, 10_000 }) {
            var state = NewRun($"resume-highest-{highestClear}", (ulong)highestClear + 1);
            SetInt(state, "HighestFloorCleared", highestClear);
            SetInt(state, "CheckpointFloor", highestClear > 5 ? 5 : 0);
            SetInt(state, "RecoveryAllowanceRemaining", 0);
            Set(state, "Status", highestClear % 2 == 0 ? TowerRunStatus.Extracted : TowerRunStatus.Defeated);
            var oldAttempt = IntProperty(state, "AttemptGeneration");

            Invoke(state, "RestartFromCheckpoint");

            Equal(highestClear + 1, IntProperty(state, "CurrentFloor"),
                $"restart resumes immediately after highest cleared floor {highestClear}");
            Equal(oldAttempt + 1, IntProperty(state, "AttemptGeneration"), "resume creates exactly one new attempt");
            Equal("Active", EnumProperty(state, "Status"), "defeat or extraction resumes an active run");
            Equal(0, IntProperty(state, "RecoveryAllowanceRemaining"), "resume preserves the run-wide recovery allowance");
        }

        var endless = NewRun("resume-endless", 10);
        SetInt(endless, "HighestFloorCleared", 100_000);
        Set(endless, "Status", TowerRunStatus.Extracted);
        Invoke(endless, "RestartFromCheckpoint");
        Equal(100_001, IntProperty(endless, "CurrentFloor"), "restart resumes safely beyond the prototype's final floor");
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

    private static void RecoveryAllowanceIsRunWideAndCombatLifecycleIsExplicit() {
        var state = NewRun("run-wide-recovery", 998);
        Equal("NotStarted", EnumProperty(state, "EncounterLifecycle"), "new run starts outside combat");
        var firstManifest = BuildManifest(state);
        var enemySlots = Enumerable.Range(0, CollectionCount(firstManifest, "Enemies")).ToArray();
        Check(!(bool)Invoke(state, "TryBeginEncounter", new ulong[] { 701 }, Array.Empty<int>()),
            "combat cannot start before every manifest enemy is admitted");
        Check((bool)Invoke(state, "TryBeginEncounter", new ulong[] { 701 }, enemySlots),
            "a complete admitted roster starts combat");
        Equal("InCombat", EnumProperty(state, "EncounterLifecycle"), "combat start is explicit");
        Check((bool)Invoke(state, "ScheduleEncounterRecovery"), "the first active interruption consumes the run replay");
        Equal(0, IntProperty(state, "RecoveryAllowanceRemaining"), "the replay is consumed once for the whole run");
        Equal("RecoveryPending", EnumProperty(state, "EncounterLifecycle"), "recovery is a safe pre-combat lifecycle");

        var replayManifest = BuildManifest(state);
        enemySlots = Enumerable.Range(0, CollectionCount(replayManifest, "Enemies")).ToArray();
        Check((bool)Invoke(state, "TryBeginEncounter", new ulong[] { 701 }, enemySlots),
            "the replayed encounter can begin after recovery preparation");
        Equal("Defeat", Apply(state, replayManifest, enemySlots.Length, 0, enemySlots.Length),
            "a later player defeat resolves normally after the replay is spent");
        Invoke(state, "RestartFromCheckpoint");
        Equal(0, IntProperty(state, "RecoveryAllowanceRemaining"), "checkpoint restart preserves the exhausted run quota");

        var secondAttemptManifest = BuildManifest(state);
        enemySlots = Enumerable.Range(0, CollectionCount(secondAttemptManifest, "Enemies")).ToArray();
        Check((bool)Invoke(state, "TryBeginEncounter", new ulong[] { 701 }, enemySlots),
            "a new checkpoint attempt may enter combat");
        Check(!(bool)Invoke(state, "ScheduleEncounterRecovery"), "a second technical interruption cannot replay");
        Equal("Defeated", EnumProperty(state, "Status"), "an exhausted interruption becomes a checkpoint defeat");
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

    private static void StandardArenaRoomIsReusedAcrossFortyFloors() {
        Equal("WizardCity/WC_Golem_Tower", TowerZonePaths.EntryZone, "existing Golem Court entry path");
        Equal(2, TowerZonePaths.GetPhysicalRoomNumber(1), "logical floor 1 starts in room 2 after the sanctuary");
        for (var floor = 1; floor <= 40; floor++) {
            var path = TowerZonePaths.GetRoomPath(floor);
            Check(TowerZonePaths.TryGetRoomNumber(path, out var actualRoom), $"floor {floor} path is recognized as a tower room");
            Equal(2, actualRoom, $"floor {floor} maps to the standardized arena");
        }
    }

    private static void FloorClearLeavesTheRunReadyForAnUpstairsAdvance() {
        var state = NewRun("arena-stair-advance", 8841);
        var firstManifest = BuildManifest(state);
        Check(!BoolProperty(state, "PendingFloorAdvance"), "an uncleared floor has no upstairs advance");
        Equal("Victory", Apply(state, firstManifest, 1, 1, 0), "floor clear commits before the staircase unlocks");
        Check(BoolProperty(state, "PendingFloorAdvance"), "victory records a pending in-arena floor advance");
        Equal(2L, LongProperty(state, "CurrentFloor"), "victory selects the next logical floor");

        var nextManifest = BuildManifest(state);
        Equal(2, IntProperty(nextManifest, "PhysicalRoomNumber"), "the next floor reuses the same physical arena");
        Equal(2L, LongProperty(nextManifest, "Floor"), "the reloaded arena receives the next logical floor");
        Check(!BoolProperty(state, "PendingFloorAdvance"), "creating the next encounter consumes the advance marker");
    }

    private static void FloorClearFeedbackReflectsCommittedProgress() {
        foreach (var floor in new[] { 1, 3, 9 }) {
            var message = NewFloorClearMessage(floor, completed: false);
            var text = GetFloorClearMessage(message);
            Check(text.Contains($"floor {floor} cleared", StringComparison.OrdinalIgnoreCase),
                $"floor {floor} feedback confirms the clear: {text}");
            Check(text.Contains("upper stairs", StringComparison.OrdinalIgnoreCase),
                $"floor {floor} feedback directs the player to the next-floor stairs: {text}");
        }

        var floorFive = GetFloorClearMessage(NewFloorClearMessage(5, completed: false));
        Check(floorFive.Contains("lower stairs", StringComparison.OrdinalIgnoreCase),
            $"floor five feedback offers a return to the sanctuary: {floorFive}");

        var endlessClear = GetFloorClearMessage(NewFloorClearMessage(100_000, completed: false));
        Check(endlessClear.Contains("floor 100000 cleared", StringComparison.OrdinalIgnoreCase),
            $"high-floor feedback remains readable: {endlessClear}");
        Check(!endlessClear.Contains("of 10", StringComparison.OrdinalIgnoreCase),
            $"endless Tower feedback has no ten-floor terminal count: {endlessClear}");
    }

    private static object NewFloorClearMessage(int floor, bool completed) {
        var type = RequireType("Imlight.CoreLib.Game.Tower.TowerFloorClearedMessage");
        var message = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create TowerFloorClearedMessage");
        Set(message, "Floor", floor);
        Set(message, "Completed", completed);
        return message;
    }

    private static string GetFloorClearMessage(object message) {
        var method = message.GetType().GetMethod("GetClientMessage", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing TowerFloorClearedMessage.GetClientMessage");
        return method.Invoke(message, null) as string
            ?? throw new InvalidOperationException("Tower floor clear message text was null");
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
        if (EnumProperty(state, "Status") == "Active" && EnumProperty(state, "EncounterLifecycle") != "InCombat") {
            var slots = Enumerable.Range(0, CollectionCount(manifest, "Enemies")).ToArray();
            Check((bool)Invoke(state, "TryBeginEncounter", new ulong[] { 701UL }, slots),
                $"resolution test starts combat with its complete roster at floor {IntProperty(state, "CurrentFloor")}, status {EnumProperty(state, "Status")}, lifecycle {EnumProperty(state, "EncounterLifecycle")}, enemies {slots.Length}");
        }
        var snapshotType = RequireType("Imlight.CoreLib.Game.Tower.TowerEncounterResolution");
        var snapshot = Activator.CreateInstance(snapshotType) ?? throw new InvalidOperationException("Could not create resolution snapshot");
        Set(snapshot, "AuthorityEpoch", epoch ?? LongProperty(state, "AuthorityEpoch"));
        Set(snapshot, "EncounterId", StringProperty(manifest, "EncounterId"));
        Set(snapshot, "PlayerCount", 1);
        Set(snapshot, "AlivePlayerCount", alivePlayers);
        Set(snapshot, "AdmittedPlayerIds", new ulong[] { 701UL });
        Set(snapshot, "AdmittedEnemySlots", Enumerable.Range(0, Math.Max(0, enemyCount)).ToArray());
        Set(snapshot, "CombatDeathEnemySlots", Enumerable.Range(0, Math.Max(0, enemyCount - aliveEnemies)).ToArray());
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
    private static uint UIntProperty(object instance, string name) => Convert.ToUInt32(GetProperty(instance, name));
    private static bool BoolProperty(object instance, string name) => Convert.ToBoolean(GetProperty(instance, name));
    private static string StringProperty(object instance, string name) => Convert.ToString(GetProperty(instance, name));
    private static string EnumProperty(object instance, string name) => GetProperty(instance, name).ToString();

    private static int CollectionCount(object instance, string name)
        => (GetProperty(instance, name) as System.Collections.ICollection)?.Count
            ?? throw new InvalidOperationException($"{name} is not an ICollection");

    private static void SetInt(object instance, string name, long value) {
        var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing public property {instance.GetType().Name}.{name}");
        property.SetValue(instance, Convert.ChangeType(value, property.PropertyType));
    }
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
