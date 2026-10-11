using System.Collections;
using System.Reflection;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Game.Tower;

internal static class TowerDuelLifecycleTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        TowerAdmissionRequiresEveryManifestEnemyAndUniqueActors();
        TowerKillsRequireCombatDeathEvidence();
        TechnicalResolutionCannotBecomeACombatWinOrLoss();
        DisconnectAndLogoutInterruptTowerDuelsWithoutFleeing();
        InterruptedEncounterConsumesOneRecoveryAndRestoresEntryResources();
        Console.WriteLine("PASS: tower duel admission, death evidence, outcome classification, and interruption policy");
    }

    private static void TowerAdmissionRequiresEveryManifestEnemyAndUniqueActors() {
        var state = TowerRunState.Create("duel-admission", 991, 1001);
        var manifest = state.BuildCurrentManifest();
        var roster = NewRoster(manifest);

        Check(!(bool)Get(roster, "IsComplete"), "empty tower roster is incomplete");
        var expected = (IList)Get(manifest, "Enemies");
        var admit = Method(roster, "AdmitEnemy");
        Check((bool)Method(roster, "AdmitPlayer").Invoke(roster, [0x100UL])!, "tower player can be admitted to the duel roster");
        var firstSlotAdmitted = (bool)admit.Invoke(roster, [manifest.EncounterId, 0, 0xA11UL])!;
        Check(firstSlotAdmitted, "first expected tower enemy can be admitted");
        Check(!(bool)admit.Invoke(roster, [manifest.EncounterId, 0, 0xA12UL])!, "a second actor cannot reuse an admitted slot");
        Check(!(bool)admit.Invoke(roster, ["stale-encounter", 1, 0xA13UL])!, "a stale encounter cannot add an enemy");

        if (expected.Count == 2) {
            Check(!(bool)Get(roster, "IsComplete"), "one of two manifest enemies is not a complete admission");
            Check((bool)admit.Invoke(roster, [manifest.EncounterId, 1, 0xA14UL])!, "the second expected tower enemy can be admitted");
        }

        Check((bool)Get(roster, "IsComplete"), "roster is complete only after every manifest slot is admitted");
        Check(!(bool)admit.Invoke(roster, [manifest.EncounterId, expected.Count, 0xA15UL])!, "an extra enemy outside the manifest is rejected");

        var twoEnemyState = TowerRunState.Create("duel-missing-enemy", 993, 1003);
        twoEnemyState.CurrentFloor = 10;
        var twoEnemyManifest = twoEnemyState.BuildCurrentManifest();
        var incompleteRoster = NewRoster(twoEnemyManifest);
        Check((bool)Method(incompleteRoster, "AdmitPlayer").Invoke(incompleteRoster, [0x300UL])!, "player is admitted for missing-enemy resolution test");
        Check((bool)Method(incompleteRoster, "AdmitEnemy").Invoke(incompleteRoster,
            [twoEnemyManifest.EncounterId, 0, 0xA20UL])!, "one floor 10 enemy is admitted");
        var incompleteResolution = Invoke(incompleteRoster, "CreateResolution", 1L, 1, 1);
        Equal("InvalidSnapshot", Invoke(twoEnemyState, "ApplyResolution", incompleteResolution).ToString(),
            "an unadmitted manifest enemy makes the resolution invalid instead of a floor clear");
    }

    private static void TowerKillsRequireCombatDeathEvidence() {
        var state = TowerRunState.Create("duel-deaths", 992, 1002);
        var manifest = state.BuildCurrentManifest();
        var roster = NewRoster(manifest);
        var enemies = (IList)Get(manifest, "Enemies");
        var admit = Method(roster, "AdmitEnemy");
        Check((bool)Method(roster, "AdmitPlayer").Invoke(roster, [0x200UL])!, "tower player is included in the resolution roster");
        var death = Method(roster, "RecordCombatDeath");

        for (var slot = 0; slot < enemies.Count; slot++) {
            var actorObjectId = (ulong)(0xB00 + slot);
            Check(!(bool)death.Invoke(roster, [slot, actorObjectId])!, "unadmitted actor cannot prove a kill");
            Check((bool)admit.Invoke(roster, [manifest.EncounterId, slot, actorObjectId])!, $"manifest enemy slot {slot} is admitted");
        }

        var resolution = Invoke(roster, "CreateResolution", 1L, 1, 1);
        Check((bool)Get(roster, "IsComplete"), "admission remains complete before kill evidence");
        Equal(0, ((int[])Get(resolution, "CombatDeathEnemySlots")).Length,
            "being absent or unobserved does not create a combat-death proof");
        Equal("EncounterContinues", Invoke(state, "ApplyResolution", resolution).ToString(),
            "a complete living roster cannot clear a floor without combat-death evidence");
        Equal(1, state.CurrentFloor, "no death evidence leaves the run on its current floor");

        for (var slot = 0; slot < enemies.Count; slot++) {
            var actorObjectId = (ulong)(0xB00 + slot);
            Check(!(bool)death.Invoke(roster, [slot, actorObjectId + 100])!, "another actor cannot claim this slot's death");
            Check((bool)death.Invoke(roster, [slot, actorObjectId])!, $"round resolution can record death for slot {slot}");
            Check(!(bool)death.Invoke(roster, [slot, actorObjectId])!, "the same death evidence cannot be counted twice");
        }

        resolution = Invoke(roster, "CreateResolution", 1L, 1, 1);
        var admittedSlots = (int[])Get(resolution, "AdmittedEnemySlots");
        var deathSlots = (int[])Get(resolution, "CombatDeathEnemySlots");
        Equal(enemies.Count, admittedSlots.Length, "resolution carries the full admitted roster");
        Equal(enemies.Count, deathSlots.Length, "resolution carries one death proof per manifest enemy");
        Equal("Victory", Invoke(state, "ApplyResolution", resolution).ToString(),
            "complete admission plus server-recorded combat deaths clears the floor");
    }

    private static void TechnicalResolutionCannotBecomeACombatWinOrLoss() {
        var policyType = Type("Imlight.CoreLib.Game.Tower.TowerDuelOutcomePolicy");
        var classify = policyType.GetMethod("Classify", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing TowerDuelOutcomePolicy.Classify");
        foreach (var result in new[] { "EncounterContinues", "StaleAuthority", "NotCurrentEncounter", "InvalidSnapshot", "RunCompleted", "RunNotActive" }) {
            var classification = classify.Invoke(null, [Enum.Parse(typeof(TowerResolutionResult), result)]).ToString();
            Equal("TechnicalAbort", classification, $"{result} uses a technical abort");
        }
        Equal("Victory", classify.Invoke(null, [TowerResolutionResult.Victory])!.ToString(), "only a committed clear is a player victory");
        Equal("Defeat", classify.Invoke(null, [TowerResolutionResult.Defeat])!.ToString(), "only a committed loss is a player defeat");
    }

    private static void DisconnectAndLogoutInterruptTowerDuelsWithoutFleeing() {
        var policyType = Type("Imlight.CoreLib.Game.Tower.TowerDuelInterruptionPolicy");
        var classify = policyType.GetMethod("Classify", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing TowerDuelInterruptionPolicy.Classify");
        Equal("TechnicalAbort", classify.Invoke(null, [true, false])!.ToString(), "unexpected tower disconnect or logout aborts the encounter");
        Equal("NativeFlee", classify.Invoke(null, [false, false])!.ToString(), "ordinary duel disconnect keeps native flee behavior");
        Equal("IntentionalFlee", classify.Invoke(null, [true, true])!.ToString(), "intentional tower flee remains a combat action");
    }

    private static void InterruptedEncounterConsumesOneRecoveryAndRestoresEntryResources() {
        var state = TowerRunState.Create("duel-recovery", 994, 1004);
        var firstManifest = state.BuildCurrentManifest();
        Check(state.CaptureFloorEntryResource(0x400UL, 88, 36), "first admission saves floor-entry HP and mana");
        Check(state.ScheduleEncounterRecovery(), "a technical interruption schedules one recovery");
        Equal(0, state.RecoveryAllowanceRemaining, "the interruption consumes this floor's recovery allowance");
        Check(state.RecoveryResourcesPending, "recovery makes the saved resource snapshot pending");

        var recoveredManifest = state.BuildCurrentManifest();
        Check(firstManifest.EncounterId != recoveredManifest.EncounterId,
            "recovery creates a new encounter identity so the physical room is rebuilt");
        Check(!state.ScheduleEncounterRecovery(), "a second interruption cannot restore resources again this floor");
        Check(state.MarkRecoveryResourcesRestored(0x400UL), "the wizard can consume the pending resource restoration");
        Check(!state.RecoveryResourcesPending, "the pending restore clears after the wizard is restored");
        var snapshot = state.FloorEntryResources.Single();
        Equal(88, snapshot.Health, "recovery restores floor-entry health");
        Equal(36, snapshot.Mana, "recovery restores floor-entry mana");
    }

    private static object NewRoster(TowerEncounterManifest manifest) {
        var type = Type("Imlight.CoreLib.Game.Tower.TowerEncounterRoster");
        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [manifest], culture: null)
            ?? throw new InvalidOperationException("Could not create TowerEncounterRoster");
    }

    private static object Invoke(object target, string name, params object[] args)
        => Method(target, name).Invoke(target, args)
            ?? throw new InvalidOperationException($"{name} returned null");

    private static MethodInfo Method(object target, string name)
        => target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {target.GetType().Name}.{name}");

    private static object Get(object target, string name)
        => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? throw new InvalidOperationException($"Missing {target.GetType().Name}.{name}");

    private static Type Type(string name)
        => CoreAssembly.GetType(name) ?? throw new InvalidOperationException($"Missing {name}");

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
