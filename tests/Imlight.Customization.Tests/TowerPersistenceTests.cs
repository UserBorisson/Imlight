using System.Reflection;
using System.Collections;
using Imlight.CoreLib.Game.Gifts;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

internal static class TowerPersistenceTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
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
            var collection = Activator.CreateInstance(collectionType, store)
                ?? throw new InvalidOperationException("Could not create TowerRunCollection");
            var state = Call(collection, "CreateRun", "durable-run", 812345UL, 0xD1CEB00CUL);
            Equal(1L, LongProperty(state, "AuthorityEpoch"), "created durable run epoch");

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

    private static object Call(object instance, string methodName, params object[] args) {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{methodName}");
        return method.Invoke(instance, args)
            ?? throw new InvalidOperationException($"{methodName} returned null");
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

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) { throw new InvalidOperationException($"{message}: expected {expected}, got {actual}"); }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
