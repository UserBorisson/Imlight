using System.Reflection;
using Imlight.Common;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.WizardData.Models.Player;
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
            var unbackedAward = CreateAward("forged/no-outbox", wizardId, 1, 1, 1, []);
            Equal("NotEligible", EnumProperty(Call(collection, "Commit", unbackedAward), "Status"),
                "a client-side award request without a durable run outbox entry is rejected");
            Equal(100, LoadWizard(store, wizardId)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(),
                "an unbacked award request leaves character XP unchanged");

            var award = CreateAward("run-1/floor-1", wizardId, 1, 1, 1, [9000101U]);
            SeedAwardOutbox(store, award);

            var result = Call(collection, "Commit", award);
            Equal("Awarded", EnumProperty(result, "Status"), "first award commits");
            Check(BoolProperty(result, "IsNewlyAwarded"), "a new award may show its client reward notice");
            Equal(1, IntProperty(result, "LevelsAwarded"), "first clear grants exactly one native level");
            Equal(2, IntProperty(result, "LevelAfter"), "first clear advances to the next native level");
            Equal(1, IntProperty(result, "TicketsAwarded"), "first clear grants its configured Tickets");
            Equal(1, CollectionCount(result, "ItemIds"), "one item is recorded on the receipt");
            var itemId = FirstItemId(result);
            var wizard = LoadWizard(store, wizardId);
            Equal(2, wizard["MagicSchoolBehavior"]["Level"].Value<int>(), "native level is committed with the receipt");
            Equal(2, wizard["GameStats"]["Level"].Value<int>(), "game stats level is committed atomically");
            var levelThreshold = wizard["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>();
            Check(levelThreshold > 100, "XP advances to the exact next-level threshold");
            Equal(1, wizard["GameStats"]["m_currentPvPCurrency"].Value<int>(), "native Blue Arena Ticket wallet receives the durable award");
            Equal(3, wizard["InventoryBehavior"]["InventoryItemIds"].Count(), "inventory reference commits with the item");
            Equal(itemId, wizard["InventoryBehavior"]["InventoryItemIds"].Last().Value<ulong>(), "recipient inventory references the stored item id");

            using (var session = store.OpenSession()) {
                Check(session.Load<JObject>($"tower-award-items/{wizardId}/{itemId}") is not null, "item document is committed");
                var progress = session.Load<JObject>($"tower-progressions/{wizardId}");
                Equal(1, progress["HighestFloorCleared"].Value<int>(), "permanent highest-floor progression commits");
                Equal(0, progress["Tickets"].Value<int>(), "legacy Tower Tickets are no longer a spendable parallel balance");
                Equal(1, progress["PermanentClears"]["Ranges"].Count(), "compact permanent history records the cleared floor");
            }

            var callsBeforeRetry = itemCounter;
            var retry = Call(collection, "Commit", award);
            Equal("AlreadyAwarded", EnumProperty(retry, "Status"), "retry returns the prior receipt");
            Check(!BoolProperty(retry, "IsNewlyAwarded"), "an idempotent award retry does not repeat its client reward notice");
            Equal(itemId, FirstItemId(retry), "retry returns committed item ids");
            Equal(1, CollectionCount(retry, "Items"), "retry can reconcile the committed item into a live session");
            Equal(callsBeforeRetry, itemCounter, "receipt lookup avoids recreating items");
            Equal(levelThreshold, LoadWizard(store, wizardId)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "retry cannot double level progress");
            Equal(3, LoadWizard(store, wizardId)["InventoryBehavior"]["InventoryItemIds"].Count(), "retry cannot double inventory references");

            var repeatClear = CreateAward("run-2/floor-1", wizardId, 1, 1, 2, []);
            SeedAwardOutbox(store, repeatClear);
            var repeatResult = Call(collection, "Commit", repeatClear);
            Equal("Awarded", EnumProperty(repeatResult, "Status"), "a distinct repeat clear earns a fresh Ticket receipt");
            Equal(0, IntProperty(repeatResult, "LevelsAwarded"), "a repeat clear grants no native levels");
            Equal(2, IntProperty(repeatResult, "TicketsAwarded"), "repeat clear still grants Tickets");
            Equal(3, IntProperty(repeatResult, "TicketsAfter"), "Ticket balance accumulates across receipts");
            Equal(2, LoadWizard(store, wizardId)["MagicSchoolBehavior"]["Level"].Value<int>(), "repeat clear keeps level unchanged");
            Equal(3, LoadWizard(store, wizardId)["GameStats"]["m_currentPvPCurrency"].Value<int>(), "native Blue Arena Tickets accumulate repeat awards");

            var modernWizard = wizardId + 20;
            SeedWizard(store, modernWizard, [], []);
            var modern = CreateAward("modern/floor-11", modernWizard, 11, 11, 6, []);
            SeedAwardOutbox(store, modern, 1);
            Equal(20, IntProperty(Call(collection, "Commit", modern), "TicketsAwarded"), "band two first-clear total is twenty");
            Equal(20, IntProperty(Call(collection, "Commit", modern), "TicketsAfter"), "duplicate modern receipt cannot repeat bonus");
            var modernRepeat = CreateAward("modern-repeat/floor-11", modernWizard, 11, 11, 6, []);
            SeedAwardOutbox(store, modernRepeat, 1);
            Equal(6, IntProperty(Call(collection, "Commit", modernRepeat), "TicketsAwarded"), "band two repeat reward is six");
            Equal(2, LoadWizard(store, modernWizard)["MagicSchoolBehavior"]["Level"].Value<int>(), "repeat does not level twice");

            var failingWizard = wizardId + 1;
            SeedWizard(store, failingWizard, [201UL], []);
            var factoryCalls = 0;
            WizClientObjectItem FailsOnSecond(uint templateId) {
                if (Interlocked.Increment(ref factoryCalls) == 2) { throw new InvalidOperationException("injected item creation failure"); }
                return CreateItem(templateId);
            }
            var failingCollection = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) FailsOnSecond);
            var failedAward = CreateAward("run-fail/floor-2", failingWizard, 2, 2, 1, [9000201U, 9000202U]);
            SeedAwardOutbox(store, failedAward);
            Equal("RetryableFailure", EnumProperty(Call(failingCollection, "Commit", failedAward), "Status"), "factory exception aborts the entire transaction");
            Equal(100, LoadWizard(store, failingWizard)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "failed transaction does not partially grant XP");
            Equal(1, LoadWizard(store, failingWizard)["MagicSchoolBehavior"]["Level"].Value<int>(), "failed transaction does not partially grant a level");
            Equal(1, LoadWizard(store, failingWizard)["InventoryBehavior"]["InventoryItemIds"].Count(), "failed transaction does not append inventory references");
            using (var session = store.OpenSession()) {
                Check(session.Load<JObject>($"tower-awards/{failingWizard}/run-fail%2Ffloor-2") is null, "failed transaction leaves no receipt");
            }

            var retryCollection = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) CreateItem);
            Equal("Awarded", EnumProperty(Call(retryCollection, "Commit", failedAward), "Status"), "failed award can be safely retried");

            var fullWizard = wizardId + 2;
            SeedWizard(store, fullWizard, [301UL, 302UL, 303UL], [999UL]);
            var fullCollection = CreateCollection(collectionType, store, 4, (Func<uint, WizClientObjectItem>) CreateItem);
            var fullAward = CreateAward("run-full/floor-1", fullWizard, 1, 1, 1, [9000301U]);
            SeedAwardOutbox(store, fullAward);
            Equal("InventoryFull", EnumProperty(Call(fullCollection, "Commit", fullAward), "Status"), "capacity rejection is safe");
            Equal(100, LoadWizard(store, fullWizard)["MagicSchoolBehavior"]["ExperiencePoints"].Value<int>(), "capacity rejection commits no XP");

            var concurrentWizard = wizardId + 3;
            SeedWizard(store, concurrentWizard, [401UL], []);
            var concurrentA = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) CreateItem);
            var concurrentB = CreateCollection(collectionType, store, 10, (Func<uint, WizClientObjectItem>) CreateItem);
            var concurrentAward = CreateAward("run-concurrent/floor-3", concurrentWizard, 3, 3, 1, [9000401U]);
            SeedAwardOutbox(store, concurrentAward);
            var results = new object[2];
            Parallel.For(0, 2, index => results[index] = Call(index == 0 ? concurrentA : concurrentB, "Commit", concurrentAward));
            Check(results.Count(item => EnumProperty(item, "Status") == "Awarded") == 1, "exactly one concurrent award wins");
            Check(results.Count(item => EnumProperty(item, "Status") == "AlreadyAwarded") == 1, "the concurrent retry sees the committed receipt");
            Equal(2, LoadWizard(store, concurrentWizard)["MagicSchoolBehavior"]["Level"].Value<int>(), "concurrent retry cannot double levels");
            Equal(2, LoadWizard(store, concurrentWizard)["InventoryBehavior"]["InventoryItemIds"].Count(), "concurrent retry cannot double item references");

            var cappedWizard = wizardId + 4;
            SeedWizard(store, cappedWizard, [501UL], [], level: 130, experiencePoints: int.MaxValue);
            var cappedAward = CreateAward("run-capped/floor-1", cappedWizard, 1, 1, 1, []);
            SeedAwardOutbox(store, cappedAward);
            var cappedResult = Call(collection, "Commit", cappedAward);
            Equal("Awarded", EnumProperty(cappedResult, "Status"), "a max-level first clear still receives a Ticket");
            Equal(0, IntProperty(cappedResult, "LevelsAwarded"), "the true configured server cap prevents another level");
            Equal(130, LoadWizard(store, cappedWizard)["MagicSchoolBehavior"]["Level"].Value<int>(), "max-level wizard remains capped");
            Equal(1, IntProperty(cappedResult, "TicketsAfter"), "max-level clear still commits its Ticket balance");

            Console.WriteLine("PASS: first-clear level, repeat-clear Tickets, inventory, progression and award receipt commit atomically and exactly once");
        } finally {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database, hardDelete: true));
        }
    }

    private static object CreateCollection(System.Type type, IDocumentStore store, int capacity, Func<uint, WizClientObjectItem> factory) {
        var setLevel = new Action<Wizard, byte>((wizard, level) => {
            wizard.MagicSchoolBehavior.Level = level;
            wizard.GameStats.Level = level;
            wizard.GameStats.m_baseHitpoints += 20;
            wizard.GameStats.m_baseMana += 5;
            wizard.GameStats.m_powerPipBase += 0.01f;
        });
        return Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                args: [store, capacity, factory, new Func<int>(() => 130), new Func<int, int>(level => level * 100), setLevel],
                culture: null)
            ?? throw new InvalidOperationException("Could not create TowerAwardCollection");
    }

    private static object CreateAward(string awardId, ulong wizardId, long floor, long highestFloor, int tickets, uint[] itemTemplates) {
        var type = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerAwardRequest")
            ?? throw new InvalidOperationException("Missing TowerAwardRequest");
        var value = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Could not create TowerAwardRequest");
        Set(value, "RunId", awardId.Split('/')[0]);
        Set(value, "AwardId", awardId);
        Set(value, "WizardId", wizardId);
        Set(value, "ExperiencePoints", 0);
        Set(value, "Floor", floor);
        Set(value, "Tickets", tickets);
        Set(value, "HighestFloorCleared", highestFloor);
        Set(value, "ItemTemplateIds", itemTemplates);
        return value;
    }

    private static void SeedAwardOutbox(IDocumentStore store, object request, int rewardPolicyVersion = 0) {
        var runId = Convert.ToString(request.GetType().GetProperty("RunId")!.GetValue(request));
        var award = new TowerPendingAward {
            RewardPolicyVersion = rewardPolicyVersion,
            RunId = runId,
            WizardId = Convert.ToUInt64(request.GetType().GetProperty("WizardId")!.GetValue(request)),
            AwardId = Convert.ToString(request.GetType().GetProperty("AwardId")!.GetValue(request)),
            ExperiencePoints = Convert.ToInt32(request.GetType().GetProperty("ExperiencePoints")!.GetValue(request)),
            Floor = Convert.ToInt64(request.GetType().GetProperty("Floor")!.GetValue(request)),
            Tickets = Convert.ToInt32(request.GetType().GetProperty("Tickets")!.GetValue(request)),
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

    private static void SeedWizard(IDocumentStore store, ulong wizardId, ulong[] inventoryIds, ulong[] equippedIds,
                                   int level = 1, int experiencePoints = 100) {
        var wizard = JObject.FromObject(new {
            AccountId = 99UL,
            CharId = wizardId,
            MagicSchoolBehavior = new { MagicSchool = "Balance", ExperiencePoints = experiencePoints, Level = level },
            InventoryBehavior = new { InventoryItemIds = inventoryIds },
            EquipmentBehavior = new { EquippedItemIds = equippedIds },
            GameStats = new { Level = level, m_baseHitpoints = 100, m_baseMana = 80, m_powerPipBase = 0.1f, m_currentEventCurrency1 = 0 },
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
    private static int IntProperty(object instance, string propertyName) => Convert.ToInt32(GetProperty(instance, propertyName));
    private static bool BoolProperty(object instance, string propertyName) => Convert.ToBoolean(GetProperty(instance, propertyName));
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
