using System.Reflection;
using Imlight.Common;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Game.Tower;
using Imcodec.ObjectProperty.TypeCache;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

internal static class TowerAwardTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        var configDirectory = Path.Combine(Path.GetTempPath(), "tower-award-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "test.ini");
        File.WriteAllText(configPath, "[Character]\nMaxLevel=130\n");
        ConfigurationManager.Initialize(configPath);

        var database = "LocalTowerAwardTests-" + Guid.NewGuid().ToString("N");
        using var store = new DocumentStore { Urls = ["http://127.0.0.1:8180"], Database = database }.Initialize();
        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));

        try {
            var wizardId = 812346UL;
            SeedWizard(store, wizardId, [101UL, 102UL], [999UL]);
            var collectionType = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerAwardCollection")
                ?? throw new InvalidOperationException("Missing TowerAwardCollection");
            long itemCounter = 6000000000000000000L;
            WizClientObjectItem CreateItem(uint templateId) => new() {
                m_templateID = templateId,
                m_globalID = (ulong) Interlocked.Increment(ref itemCounter),
            };
            var collection = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) CreateItem);
            var unbackedAward = CreateAward("forged/no-outbox", wizardId, 999, 1, []);
            Equal("NotEligible", EnumProperty(Call(collection, "Commit", unbackedAward), "Status"),
                "a client-side award request without a durable run outbox entry is rejected");
            Equal(100, LoadWizard(store, wizardId)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(),
                "an unbacked award request leaves character XP unchanged");

            var award = CreateAward("run-1/floor-1", wizardId, 25, 1, [9000101U]);
            SeedAwardOutbox(store, award);

            var result = Call(collection, "Commit", award);
            Equal("Awarded", EnumProperty(result, "Status"), "first award commits");
            Equal(1, CollectionCount(result, "ItemIds"), "one item is recorded on the receipt");
            var itemId = FirstItemId(result);
            var wizard = LoadWizard(store, wizardId);
            Equal(125, wizard["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "XP is committed with the receipt");
            Equal(3, wizard["InventoryBehavior"]["InventoryItemIds"].Count(), "inventory reference commits with the item");
            Equal(itemId, wizard["InventoryBehavior"]["InventoryItemIds"].Last().Value<ulong>(), "recipient inventory references the stored item id");

            using (var session = store.OpenSession()) {
                Check(session.Load<JObject>($"tower-award-items/{wizardId}/{itemId}") is not null, "item document is committed");
                var progress = session.Load<JObject>($"tower-progressions/{wizardId}");
                Equal(1, progress["HighestFloorCleared"].Value<int>(), "permanent highest-floor progression commits");
            }

            var callsBeforeRetry = itemCounter;
            var retry = Call(collection, "Commit", award);
            Equal("AlreadyAwarded", EnumProperty(retry, "Status"), "retry returns the prior receipt");
            Equal(itemId, FirstItemId(retry), "retry returns committed item ids");
            Equal(1, CollectionCount(retry, "Items"), "retry can reconcile the committed item into a live session");
            Equal(callsBeforeRetry, itemCounter, "receipt lookup avoids recreating items");
            Equal(125, LoadWizard(store, wizardId)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "retry cannot double XP");
            Equal(3, LoadWizard(store, wizardId)["InventoryBehavior"]["InventoryItemIds"].Count(), "retry cannot double inventory references");

            var failingWizard = wizardId + 1;
            SeedWizard(store, failingWizard, [201UL], []);
            var factoryCalls = 0;
            WizClientObjectItem FailsOnSecond(uint templateId) {
                if (Interlocked.Increment(ref factoryCalls) == 2) { throw new InvalidOperationException("injected item creation failure"); }
                return CreateItem(templateId);
            }
            var failingCollection = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) FailsOnSecond);
            var failedAward = CreateAward("run-fail/floor-2", failingWizard, 60, 2, [9000201U, 9000202U]);
            SeedAwardOutbox(store, failedAward);
            Equal("RetryableFailure", EnumProperty(Call(failingCollection, "Commit", failedAward), "Status"), "factory exception aborts the entire transaction");
            Equal(100, LoadWizard(store, failingWizard)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "failed transaction does not partially grant XP");
            Equal(1, LoadWizard(store, failingWizard)["InventoryBehavior"]["InventoryItemIds"].Count(), "failed transaction does not append inventory references");
            using (var session = store.OpenSession()) {
                Check(session.Load<JObject>($"tower-awards/{failingWizard}/run-fail%2Ffloor-2") is null, "failed transaction leaves no receipt");
            }

            var retryCollection = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) CreateItem);
            Equal("Awarded", EnumProperty(Call(retryCollection, "Commit", failedAward), "Status"), "failed award can be safely retried");

            var fullWizard = wizardId + 2;
            SeedWizard(store, fullWizard, [301UL, 302UL, 303UL], [999UL]);
            var fullCollection = CreateCollection(collectionType, store, 4, (Func<uint, WizClientObjectItem>) CreateItem);
            var fullAward = CreateAward("run-full/floor-1", fullWizard, 10, 1, [9000301U]);
            SeedAwardOutbox(store, fullAward);
            Equal("InventoryFull", EnumProperty(Call(fullCollection, "Commit", fullAward), "Status"), "capacity rejection is safe");
            Equal(100, LoadWizard(store, fullWizard)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "capacity rejection commits no XP");

            var concurrentWizard = wizardId + 3;
            SeedWizard(store, concurrentWizard, [401UL], []);
            var concurrentA = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) CreateItem);
            var concurrentB = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) CreateItem);
            var concurrentAward = CreateAward("run-concurrent/floor-3", concurrentWizard, 30, 3, [9000401U]);
            SeedAwardOutbox(store, concurrentAward);
            var results = new object[2];
            Parallel.For(0, 2, index => results[index] = Call(index == 0 ? concurrentA : concurrentB, "Commit", concurrentAward));
            Check(results.Count(item => EnumProperty(item, "Status") == "Awarded") == 1, "exactly one concurrent award wins");
            Check(results.Count(item => EnumProperty(item, "Status") == "AlreadyAwarded") == 1, "the concurrent retry sees the committed receipt");
            Equal(130, LoadWizard(store, concurrentWizard)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "concurrent retry cannot double XP");
            Equal(2, LoadWizard(store, concurrentWizard)["InventoryBehavior"]["InventoryItemIds"].Count(), "concurrent retry cannot double item references");

            Console.WriteLine("PASS: recipient-side XP, inventory, item, progression and award receipt commit atomically and exactly once");
        } finally {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database, hardDelete: true));
        }
    }

    private static object CreateCollection(System.Type type, IDocumentStore store, int capacity, Func<uint, WizClientObjectItem> factory)
        => Activator.CreateInstance(type, store, capacity, factory)
            ?? throw new InvalidOperationException("Could not create TowerAwardCollection");

    private static object CreateAward(string awardId, ulong wizardId, int xp, int highestFloor, uint[] itemTemplates) {
        var type = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerAwardRequest")
            ?? throw new InvalidOperationException("Missing TowerAwardRequest");
        var value = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Could not create TowerAwardRequest");
        Set(value, "RunId", awardId.Split('/')[0]);
        Set(value, "AwardId", awardId);
        Set(value, "WizardId", wizardId);
        Set(value, "ExperiencePoints", xp);
        Set(value, "HighestFloorCleared", highestFloor);
        Set(value, "ItemTemplateIds", itemTemplates);
        return value;
    }

    private static void SeedAwardOutbox(IDocumentStore store, object request) {
        var runId = Convert.ToString(request.GetType().GetProperty("RunId")!.GetValue(request));
        var award = new TowerPendingAward {
            RunId = runId,
            WizardId = Convert.ToUInt64(request.GetType().GetProperty("WizardId")!.GetValue(request)),
            AwardId = Convert.ToString(request.GetType().GetProperty("AwardId")!.GetValue(request)),
            ExperiencePoints = Convert.ToInt32(request.GetType().GetProperty("ExperiencePoints")!.GetValue(request)),
            HighestFloorCleared = Convert.ToInt32(request.GetType().GetProperty("HighestFloorCleared")!.GetValue(request)),
            ItemTemplateIds = (uint[])request.GetType().GetProperty("ItemTemplateIds")!.GetValue(request),
        };
        var run = TowerRunState.Create(runId, award.WizardId, 17);
        run.PendingAwards.Add(award);
        using var session = store.OpenSession();
        var runDocumentId = "tower-runs/" + Uri.EscapeDataString(runId);
        session.Store(run, runDocumentId);
        session.Advanced.GetMetadataFor(run)["@collection"] = "TowerRuns";
        session.SaveChanges();
    }

    private static void SeedWizard(IDocumentStore store, ulong wizardId, ulong[] inventoryIds, ulong[] equippedIds) {
        var wizard = JObject.FromObject(new {
            AccountId = 99UL,
            CharId = wizardId,
            MagicSchoolBehavior = new { MagicSchool = "Balance", ExperiencePoints = 100, Level = 1 },
            InventoryBehavior = new { InventoryItemIds = inventoryIds },
            EquipmentBehavior = new { EquippedItemIds = equippedIds },
            GameStats = new { Level = 1, m_baseHitpoints = 100, m_baseMana = 80, m_powerPipBase = 0.1f },
            PreservedField = "keep",
        });
        using var session = store.OpenSession();
        session.Store(wizard, "tower-fixtures/" + wizardId);
        session.Advanced.GetMetadataFor(wizard)["@collection"] = "Wizards";
        session.Advanced.GetMetadataFor(wizard)["Raven-Clr-Type"] = "Imlight.CoreLib.WizardData.Models.Player.Wizard, Imlight.CoreLib";
        session.SaveChanges();
    }

    private static JObject LoadWizard(IDocumentStore store, ulong wizardId) {
        using var session = store.OpenSession();
        return session.Load<JObject>("tower-fixtures/" + wizardId)
            ?? throw new InvalidOperationException("Test wizard disappeared");
    }

    private static ulong FirstItemId(object result)
        => Convert.ToUInt64((GetProperty(result, "ItemIds") as System.Collections.IEnumerable)?.Cast<object>().FirstOrDefault()
            ?? throw new InvalidOperationException("Award result contains no item IDs"));

    private static object Call(object instance, string methodName, params object[] args) {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{methodName}");
        return method.Invoke(instance, args)
            ?? throw new InvalidOperationException($"{methodName} returned null");
    }

    private static object GetProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{propertyName}");

    private static string EnumProperty(object instance, string propertyName) => GetProperty(instance, propertyName).ToString();
    private static int CollectionCount(object instance, string propertyName)
        => (GetProperty(instance, propertyName) as System.Collections.ICollection)?.Count
            ?? throw new InvalidOperationException($"{propertyName} is not a collection");

    private static void Set(object instance, string propertyName, object value) {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{propertyName}");
        property.SetValue(instance, value);
    }

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) { throw new InvalidOperationException($"{message}: expected {expected}, got {actual}"); }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
