using System.Reflection;
using System.Collections;
using Imlight.Common;
using Imlight.CoreLib.Game.Gifts;
using Newtonsoft.Json.Linq;
using WizClientObjectItem = Imcodec.ObjectProperty.TypeCache.WizClientObjectItem;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

internal static class TowerPersistenceTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        var configDirectory = Path.Combine(Path.GetTempPath(), "tower-persistence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "test.ini");
        File.WriteAllText(configPath, "[Character]\nMaxLevel=130\n");
        ConfigurationManager.Initialize(configPath);

        var database = "LocalTowerRunTests-" + Guid.NewGuid().ToString("N");
        using var store = new DocumentStore {
            Urls = ["http://127.0.0.1:8180"],
            Database = database,
            Conventions = { UseOptimisticConcurrency = true },
        }.Initialize();
        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));

        try {
            var collectionType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerRunCollection")
                ?? throw new InvalidOperationException("Missing TowerRunCollection");
            ServerOwnershipLeaseBlocksConcurrentTakeover(store);
            var collection = Activator.CreateInstance(collectionType, store)
                ?? throw new InvalidOperationException("Could not create TowerRunCollection");
            var state = Call(collection, "CreateRun", "durable-run", 812345UL, 0xD1CEB00CUL);
            Equal(1L, LongProperty(state, "AuthorityEpoch"), "created durable run epoch");

            SafeBoundaryTakeoverDoesNotConsumeEncounterRecovery(store, collectionType);
            ProcessIncarnationTakeoverConsumesOneRecoveryAndPreservesFloorEntryResources(store, collectionType);

            var pendingAwardType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerPendingAward")
                ?? throw new InvalidOperationException("Missing TowerPendingAward");
            var pendingAward = Activator.CreateInstance(pendingAwardType)
                ?? throw new InvalidOperationException("Could not create pending award");
            Set(pendingAward, "RunId", "durable-run");
            Set(pendingAward, "WizardId", 998877UL);
            Set(pendingAward, "AwardId", "durable-run/attempt-1/floor-1/wizard-998877");
            Set(pendingAward, "ExperiencePoints", 35);
            Set(pendingAward, "HighestFloorCleared", 1);
            ((IList)GetProperty(state, "PendingAwards")).Add(pendingAward);

            var manifest = Call(state, "BuildCurrentManifest");
            Check((bool) Call(collection, "TrySave", state, 1L), "initial manifest should persist");

            var pending = Call(collection, "LoadPendingAwards", 998877UL) as Array;
            Equal(1, pending?.Length ?? 0, "a non-owner party member recovers its unreceipted award from the owner's durable outbox");
            var receiptType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerAwardReceipt")
                ?? throw new InvalidOperationException("Missing TowerAwardReceipt");
            var receipt = Activator.CreateInstance(receiptType)
                ?? throw new InvalidOperationException("Could not create award receipt");
            using (var session = store.OpenSession()) {
                session.Store(receipt, "tower-awards/998877/durable-run%2Fattempt-1%2Ffloor-1%2Fwizard-998877");
                session.SaveChanges();
            }
            pending = Call(collection, "LoadPendingAwards", 998877UL) as Array;
            Equal(0, pending?.Length ?? 0, "a recipient receipt closes the pending award outbox entry");

            var recovered = Call(collection, "Load", "durable-run");
            Equal(StringProperty(manifest, "EncounterId"), StringProperty(Call(recovered, "BuildCurrentManifest"), "EncounterId"), "manifest survives storage reload");

            var acquired = Call(collection, "AcquireAuthority", "durable-run", 812345UL);
            Equal(2L, LongProperty(acquired, "AuthorityEpoch"), "recovery advances durable fencing token");

            // A stale copy has a valid old epoch and a plausible progress mutation. Raven must reject it.
            SetInt(recovered, "HighestFloorCleared", 1);
            Check(!(bool) Call(collection, "TrySave", recovered, 1L), "stale writer cannot overwrite recovered state");
            Equal(0, IntProperty(Call(collection, "Load", "durable-run"), "HighestFloorCleared"), "rejected stale write leaves durable state unchanged");

            var concurrentA = Call(collection, "Load", "durable-run");
            var concurrentB = Call(collection, "Load", "durable-run");
            SetInt(concurrentA, "HighestFloorCleared", 1);
            Check((bool) Call(collection, "TrySave", concurrentA, 2L), "first write at the current epoch should commit");
            SetInt(concurrentB, "HighestFloorCleared", 4);
            Check(!(bool) Call(collection, "TrySave", concurrentB, 2L), "older same-epoch revision cannot overwrite a committed transition");
            Equal(1, IntProperty(Call(collection, "Load", "durable-run"), "HighestFloorCleared"), "revision conflict preserves the winning transition");

            // Failure before SaveChanges is an in-memory mutation only; a new collection sees the committed state.
            var interrupted = Call(collection, "Load", "durable-run");
            SetInt(interrupted, "HighestFloorCleared", 9);
            var freshCollection = Activator.CreateInstance(collectionType, store)
                ?? throw new InvalidOperationException("Could not recreate TowerRunCollection");
            Equal(1, IntProperty(Call(freshCollection, "Load", "durable-run"), "HighestFloorCleared"), "uncommitted state is not visible after actor restart");

            var defeated = Call(collection, "Load", "durable-run");
            Set(defeated, "Status", Enum.Parse(CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerRunStatus")!, "Defeated"));
            Check((bool)Call(collection, "TrySave", defeated, 2L), "defeat status should persist before restart");
            var restarted = Call(collection, "CreateOrResume", 812345UL, 0xD1CEB00CUL);
            Equal("Active", GetProperty(restarted, "Status").ToString(), "defeated run resumes as active");
            Equal(1, IntProperty(restarted, "CurrentFloor"), "restart resumes at floor after the last checkpoint");
            Equal(2, IntProperty(restarted, "AttemptGeneration"), "restart advances the attempt generation");
            Equal(3L, LongProperty(restarted, "AuthorityEpoch"), "restart keeps the acquired authority epoch and fences old actors");
            Equal("Active", GetProperty(Call(collection, "Load", "durable-run"), "Status").ToString(), "restarted state is durable");

            Check((bool)Call(collection, "TryExtractRun", 812345UL), "voluntary extraction persists the checkpoint state");
            var extracted = Call(collection, "Load", "durable-run");
            Equal("Extracted", GetProperty(extracted, "Status").ToString(), "extraction is recorded before leaving the room");
            Equal(4L, LongProperty(extracted, "AuthorityEpoch"), "extraction fences the old room actor");
            var checkpointResume = Call(collection, "CreateOrResume", 812345UL, 0xD1CEB00CUL);
            Equal("Active", GetProperty(checkpointResume, "Status").ToString(), "an extracted run resumes as active");
            Equal(1, IntProperty(checkpointResume, "CurrentFloor"), "extraction before the first checkpoint restarts at floor 1");
            Equal(3, IntProperty(checkpointResume, "AttemptGeneration"), "checkpoint resume starts a new attempt");
            Equal(5L, LongProperty(checkpointResume, "AuthorityEpoch"), "checkpoint resume retains the newly acquired epoch");

            Console.WriteLine("PASS: Raven persistence, recovery fencing, defeated and extracted checkpoint restarts, party outbox replay and interrupted writes");
        } finally {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database, hardDelete: true));
        }
    }

    private static void ServerOwnershipLeaseBlocksConcurrentTakeover(IDocumentStore store) {
        var type = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerServerOwnership")
            ?? throw new InvalidOperationException("Missing TowerServerOwnership");
        var now = DateTime.UtcNow;
        Func<DateTime> clock = () => now;
        var first = NewOwnership(type, store, "server-process-a", clock);
        var second = NewOwnership(type, store, "server-process-b", clock);

        Check((bool)Call(first, "TryAcquire"), "first server process acquires the exclusive Tower lease");
        Check(!(bool)Call(second, "TryAcquire"), "a live lease blocks another process takeover");

        using var inFlightStaleWrite = store.OpenSession(new SessionOptions {
            TransactionMode = TransactionMode.ClusterWide,
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.None,
        });
        Check((bool)Call(first, "Touch", inFlightStaleWrite),
            "the original process can enlist a lease touch before takeover begins");

        var runCollectionType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerRunCollection")!;
        var wizardId = 998899UL;
        SeedFenceWizard(store, wizardId);
        var firstRuns = NewOwnedCollection(runCollectionType, store, first);
        var run = Call(firstRuns, "CreateRun", "ownership-fenced-run", wizardId, 0xC0FFEEUL);
        var pendingAward = Activator.CreateInstance(CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerPendingAward")!)!;
        Set(pendingAward, "RunId", "ownership-fenced-run");
        Set(pendingAward, "WizardId", wizardId);
        Set(pendingAward, "AwardId", "ownership-fenced-run/floor-1/wizard-998899");
        Set(pendingAward, "ExperiencePoints", 50);
        Set(pendingAward, "HighestFloorCleared", 1);
        Set(pendingAward, "ItemTemplateIds", Array.Empty<uint>());
        ((IList)GetProperty(run, "PendingAwards")).Add(pendingAward);
        var manifest = Call(run, "BuildCurrentManifest");
        var enemySlots = Enumerable.Range(0, ((IList)GetProperty(manifest, "Enemies")).Count).ToArray();
        Check((bool)Call(run, "CaptureFloorEntryResource", wizardId, 90, 50),
            "the admitted Tower participant receives a durable floor-entry resource snapshot");
        Check((bool)Call(run, "TryBeginEncounter", new ulong[] { wizardId }, enemySlots),
            "the fenced fixture enters combat with its full roster");
        Check((bool)Call(firstRuns, "TrySave", run, LongProperty(run, "AuthorityEpoch")),
            "the owner persists the combat lifecycle and pending award");

        var liveState = Call(run, "Copy");
        Check((bool)Call(firstRuns, "TryUpdateCombatResources", liveState,
                StringProperty(manifest, "EncounterId"), wizardId, 84, 42),
            "the current owner commits HP and mana atomically with the run fence");
        Equal(84, ReadFenceWizard(store, wizardId, "m_currentHitpoints"), "the current owner's HP write commits");
        Equal(42, ReadFenceWizard(store, wizardId, "m_currentMana"), "the current owner's mana write commits");

        now = now.AddSeconds(21);
        Check((bool)Call(second, "TryAcquire"), "a process can take ownership after the lease expires");
        var staleCommitRejected = false;
        try { inFlightStaleWrite.SaveChanges(); }
        catch (ClusterTransactionConcurrencyException) { staleCommitRejected = true; }
        Check(staleCommitRejected,
            "a stale cluster transaction opened before takeover cannot commit after the lease changes");
        using var staleWrite = store.OpenSession(new SessionOptions {
            TransactionMode = TransactionMode.ClusterWide,
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.None,
        });
        Check(!(bool)Call(first, "Touch", staleWrite),
            "the prior owner cannot enlist a Tower write after another process takes over");
        Check((bool)Call(second, "Touch", staleWrite),
            "the current owner can enlist a write in the same cluster transaction");
        staleWrite.SaveChanges();

        var secondRuns = NewOwnedCollection(runCollectionType, store, second);
        var acquired = Call(secondRuns, "AcquireAuthority", "ownership-fenced-run", wizardId);
        Check(acquired is not null, "the lease holder can acquire the durable run after takeover");
        Check(!(bool)Call(firstRuns, "TryUpdateCombatResources", liveState,
                StringProperty(manifest, "EncounterId"), wizardId, 1, 1),
            "the stale process cannot write character HP or mana after takeover");
        Check(!(bool)Call(firstRuns, "TrySave", liveState, LongProperty(liveState, "AuthorityEpoch")),
            "the stale process cannot save Tower run state after takeover");

        var awardType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerAwardCollection")!;
        long itemCounter = 7000000000000000000L;
        WizClientObjectItem CreateItem(uint templateId) => new() {
            m_templateID = templateId,
            m_globalID = (ulong)Interlocked.Increment(ref itemCounter),
        };
        var staleAwards = NewOwnedAwardCollection(awardType, store, first, (Func<uint, WizClientObjectItem>)CreateItem);
        var staleAward = Activator.CreateInstance(CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerAwardRequest")!)!;
        Set(staleAward, "RunId", "ownership-fenced-run");
        Set(staleAward, "AwardId", "ownership-fenced-run/floor-1/wizard-998899");
        Set(staleAward, "WizardId", wizardId);
        Set(staleAward, "ExperiencePoints", 50);
        Set(staleAward, "HighestFloorCleared", 1);
        Set(staleAward, "ItemTemplateIds", Array.Empty<uint>());
        var awardResult = Call(staleAwards, "Commit", staleAward);
        Equal("RetryableFailure", GetProperty(awardResult, "Status").ToString(),
            "a stale process cannot commit a reward after takeover");
        Equal(100, ReadFenceWizard(store, wizardId, "ExperiencePoints"),
            "the rejected stale reward leaves XP unchanged");
        var currentAwards = NewOwnedAwardCollection(awardType, store, second, (Func<uint, WizClientObjectItem>)CreateItem);
        var currentAwardResult = Call(currentAwards, "Commit", staleAward);
        Equal("Awarded", GetProperty(currentAwardResult, "Status").ToString(),
            "the current process can commit the durable pending award in its cluster-wide transaction");
        Equal(150, ReadFenceWizard(store, wizardId, "ExperiencePoints"),
            "the current owner's reward transaction commits character XP");

        CallNullable(first, "Release");
        CallNullable(second, "Release");
    }

    private static object NewOwnership(Type ownershipType, IDocumentStore store, string processIncarnation,
                                       Func<DateTime> clock) {
        var constructor = ownershipType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(IDocumentStore), typeof(string), typeof(Func<DateTime>), typeof(TimeSpan)],
                modifiers: null)
            ?? throw new InvalidOperationException("TowerServerOwnership must allow a test clock and lease duration");
        return constructor.Invoke([store, processIncarnation, clock, TimeSpan.FromSeconds(20)]);
    }

    private static object NewOwnedCollection(Type collectionType, IDocumentStore store, object ownership) {
        var ownershipType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerServerOwnership")!;
        var constructor = collectionType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: [typeof(IDocumentStore), ownershipType], modifiers: null)
            ?? throw new InvalidOperationException("TowerRunCollection must accept a process ownership fence");
        return constructor.Invoke([store, ownership]);
    }

    private static object NewOwnedAwardCollection(Type collectionType, IDocumentStore store, object ownership,
                                                   Func<uint, WizClientObjectItem> createItem) {
        var ownershipType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerServerOwnership")!;
        var constructor = collectionType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: [typeof(IDocumentStore), typeof(int), typeof(Func<uint, WizClientObjectItem>), ownershipType], modifiers: null)
            ?? throw new InvalidOperationException("TowerAwardCollection must accept a process ownership fence");
        return constructor.Invoke([store, 10, createItem, ownership]);
    }

    private static void SeedFenceWizard(IDocumentStore store, ulong wizardId) {
        var wizard = Newtonsoft.Json.Linq.JObject.FromObject(new {
            AccountId = 99UL,
            CharId = wizardId,
            MagicSchoolBehavior = new { MagicSchool = "Balance", ExperiencePoints = 100, Level = 1 },
            InventoryBehavior = new { InventoryItemIds = Array.Empty<ulong>() },
            EquipmentBehavior = new { EquippedItemIds = Array.Empty<ulong>() },
            GameStats = new {
                Level = 1, m_baseHitpoints = 100, m_baseMana = 80,
                m_currentHitpoints = 100, m_currentMana = 80, m_powerPipBase = 0.1f,
            },
        });
        using var session = store.OpenSession();
        session.Store(wizard, $"tower-fixtures/{wizardId}");
        session.Advanced.GetMetadataFor(wizard)["@collection"] = "Wizards";
        session.Advanced.GetMetadataFor(wizard)["Raven-Clr-Type"] = "Imlight.CoreLib.WizardData.Models.Player.Wizard, Imlight.CoreLib";
        session.SaveChanges();
    }

    private static int ReadFenceWizard(IDocumentStore store, ulong wizardId, string propertyName) {
        using var session = store.OpenSession();
        var wizard = session.Load<Newtonsoft.Json.Linq.JObject>($"tower-fixtures/{wizardId}")!;
        var token = propertyName is "m_currentHitpoints" or "m_currentMana"
            ? wizard["GameStats"]?[propertyName]
            : wizard["MagicSchoolBehavior"]?[propertyName];
        return token?.Value<int>() ?? throw new InvalidOperationException($"Missing wizard test field {propertyName}");
    }

    private static void SafeBoundaryTakeoverDoesNotConsumeEncounterRecovery(IDocumentStore store, Type collectionType) {
        var firstProcess = NewCollection(collectionType, store, "safe-boundary-process-a");
        var readyRun = Call(firstProcess, "CreateRun", "safe-boundary-recovery", 812347UL, 0xD1CEB00EUL);
        Call(readyRun, "BuildCurrentManifest");
        Check((bool)Call(firstProcess, "TrySave", readyRun, LongProperty(readyRun, "AuthorityEpoch")),
            "a complete manifest may be persisted before combat starts");

        var recoveredProcess = NewCollection(collectionType, store, "safe-boundary-process-b");
        var acquired = Call(recoveredProcess, "AcquireAuthority", "safe-boundary-recovery", 812347UL);
        Equal(2L, LongProperty(acquired, "AuthorityEpoch"),
            "safe-boundary takeover still rotates the authority fence");
        Equal(1, IntProperty(acquired, "RecoveryAllowanceRemaining"),
            "a process restart before a floor encounter does not spend its recovery allowance");
        var proof = GetProperty(acquired, "LastTakeoverProof");
        Check(!(bool)GetProperty(proof, "RecoveryAllowanceConsumed"),
            "safe-manifest takeover proof records no encounter recovery");
        Equal("NotStarted", GetProperty(acquired, "EncounterLifecycle").ToString(),
            "a manifest alone does not mark combat in progress");
    }

    private static void ProcessIncarnationTakeoverConsumesOneRecoveryAndPreservesFloorEntryResources(
        IDocumentStore store, Type collectionType) {
        var firstProcess = NewCollection(collectionType, store, "process-incarnation-a");
        var run = Call(firstProcess, "CreateRun", "recovery-contract", 812346UL, 0xD1CEB00DUL);
        Call(run, "BuildCurrentManifest");
        Check((bool)Call(run, "CaptureFloorEntryResource", 812346UL, 73, 41),
            "first duel admission captures this wizard's floor-entry resources");
        Check((bool)Call(firstProcess, "TrySave", run, LongProperty(run, "AuthorityEpoch")),
            "floor-entry resources persist with the current run fence");
        var manifest = GetProperty(run, "CurrentManifest");
        var admittedSlots = Enumerable.Range(0, ((IList)GetProperty(manifest, "Enemies")).Count).ToArray();
        Check((bool)Call(run, "TryBeginEncounter", new ulong[] { 812346UL }, admittedSlots),
            "the takeover fixture enters combat only with its complete admitted roster");
        Check((bool)Call(firstProcess, "TrySave", run, LongProperty(run, "AuthorityEpoch")),
            "the in-combat lifecycle and roster persist before takeover");
        var priorRevision = LongProperty(run, "Revision");

        var recoveredProcess = NewCollection(collectionType, store, "process-incarnation-b");
        var acquired = Call(recoveredProcess, "AcquireAuthority", "recovery-contract", 812346UL);
        Equal("process-incarnation-b", StringProperty(acquired, "AuthorityProcessIncarnation"),
            "takeover records the new server process incarnation");
        var acquiredRevision = LongProperty(acquired, "Revision");
        Equal(0, IntProperty(acquired, "RecoveryAllowanceRemaining"),
            "one cross-process takeover consumes the run-wide recovery allowance");
        Check((bool)GetProperty(acquired, "RecoveryResourcesPending"),
            "takeover schedules restoration from the floor-entry resource snapshot");
        var storedResources = (IList)GetProperty(acquired, "FloorEntryResources");
        Equal(1, storedResources.Count, "the admitted wizard's floor-entry snapshot survives process recovery");
        Equal(73, IntProperty(storedResources[0]!, "Health"), "recovery preserves floor-entry health");
        Equal(41, IntProperty(storedResources[0]!, "Mana"), "recovery preserves floor-entry mana");
        Check((bool)Call(acquired, "MarkRecoveryResourcesRestored", 812346UL),
            "recovery restores each wizard at most once from the floor-entry snapshot");
        Check(!(bool)GetProperty(acquired, "RecoveryResourcesPending"),
            "the restore is complete after every snapshotted wizard has re-entered");
        Check((bool)Call(recoveredProcess, "TrySave", acquired, LongProperty(acquired, "AuthorityEpoch")),
            "resource restoration acknowledgement persists with the takeover lease");

        var proof = GetProperty(acquired, "LastTakeoverProof");
        Equal(1L, LongProperty(proof, "FromEpoch"), "takeover proof names the prior authority epoch");
        Equal(priorRevision, LongProperty(proof, "PriorRevision"), "takeover proof names the exact persisted revision it observed");
        Equal(LongProperty(acquired, "AuthorityEpoch"), LongProperty(proof, "ToEpoch"),
            "takeover proof names the committed authority epoch");
        Equal(acquiredRevision, LongProperty(proof, "CommittedRevision"),
            "takeover proof is tied to the revision committed by the acquisition transaction");
        Equal("process-incarnation-a", StringProperty(proof, "FromProcessIncarnation"),
            "takeover proof names the displaced process");
        Equal("process-incarnation-b", StringProperty(proof, "ToProcessIncarnation"),
            "takeover proof names the new owner process");
        Check(!string.IsNullOrWhiteSpace(StringProperty(proof, "LeaseId")),
            "takeover proof is bound to a committed authority lease");
        Check((bool)GetProperty(proof, "RecoveryAllowanceConsumed"),
            "the proof records that cross-process recovery consumed the allowance");

        var duplicateTakeover = NewCollection(collectionType, store, "process-incarnation-c");
        Check(EnumProperty(acquired, "EncounterLifecycle") == "RecoveryPending",
            "the first recovery leaves a safe pre-combat lifecycle");
        var replayManifest = Call(acquired, "BuildCurrentManifest");
        admittedSlots = Enumerable.Range(0, ((IList)GetProperty(replayManifest, "Enemies")).Count).ToArray();
        Check((bool)Call(acquired, "TryBeginEncounter", new ulong[] { 812346UL }, admittedSlots),
            "the replay must actually enter combat before another interruption can fail the run");
        Check((bool)Call(recoveredProcess, "TrySave", acquired, LongProperty(acquired, "AuthorityEpoch")),
            "the replay in-combat lifecycle persists before a second takeover");
        var exhaustedTakeover = Call(duplicateTakeover, "AcquireAuthority", "recovery-contract", 812346UL);
        Equal("Defeated", GetProperty(exhaustedTakeover, "Status").ToString(),
            "an active interruption after quota exhaustion becomes checkpoint defeat");
        Equal(0, IntProperty(exhaustedTakeover, "RecoveryAllowanceRemaining"),
            "exhausted takeover does not replenish the run quota");
        var checkpointRestart = Call(duplicateTakeover, "CreateOrResume", 812346UL, 0xD1CEB00DUL);
        Equal("Active", GetProperty(checkpointRestart, "Status").ToString(),
            "exhausted takeover can resume at the checkpoint through CreateOrResume");
        Equal(0, IntProperty(checkpointRestart, "RecoveryAllowanceRemaining"),
            "checkpoint restart retains the exhausted quota");
        var original = Call(firstProcess, "Load", "recovery-contract");
        Check(!(bool)Call(firstProcess, "TrySave", original, 1L),
            "the displaced process cannot save after verified takeover");
    }

    private static object NewCollection(Type collectionType, IDocumentStore store, string processIncarnation) {
        var constructor = collectionType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: [typeof(IDocumentStore), typeof(string)], modifiers: null)
            ?? throw new InvalidOperationException("TowerRunCollection must accept a process incarnation");
        return constructor.Invoke([store, processIncarnation]);
    }

    private static object Call(object instance, string methodName, params object[] args) {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{methodName}");
        return method.Invoke(instance, args)
            ?? throw new InvalidOperationException($"{methodName} returned null");
    }

    private static object CallNullable(object instance, string methodName, params object[] args) {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{methodName}");
        return method.Invoke(instance, args);
    }

    private static object GetProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{propertyName}");

    private static void Set(object instance, string propertyName, object value) {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{propertyName}");
        property.SetValue(instance, value);
    }

    private static void SetInt(object instance, string propertyName, int value)
        => instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.SetValue(instance, value);

    private static int IntProperty(object instance, string propertyName) => Convert.ToInt32(GetProperty(instance, propertyName));
    private static long LongProperty(object instance, string propertyName) => Convert.ToInt64(GetProperty(instance, propertyName));
    private static string StringProperty(object instance, string propertyName) => Convert.ToString(GetProperty(instance, propertyName));
    private static string EnumProperty(object instance, string propertyName) => GetProperty(instance, propertyName).ToString();

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) { throw new InvalidOperationException($"{message}: expected {expected}, got {actual}"); }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
