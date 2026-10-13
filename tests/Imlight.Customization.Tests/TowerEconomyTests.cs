using System.Reflection;
using Imlight.CoreLib.Game.Gifts;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

internal static class TowerEconomyTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        var database = "LocalTowerEconomyTests-" + Guid.NewGuid().ToString("N");
        using var store = new DocumentStore { Urls = ["http://127.0.0.1:8180"], Database = database }.Initialize();
        store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));

        try {
            const ulong wizardId = 919191UL;
            const ulong ownedInventoryItem = 313131UL;
            const ulong ownedEquippedItem = 414141UL;
            SeedWizard(store, wizardId, ownedInventoryItem, ownedEquippedItem);
            using (var session = store.OpenSession()) {
                session.Store(new Imlight.CoreLib.Game.Tower.TowerPlayerProgression {
                    WizardId = wizardId,
                    Tickets = 20,
                }, Imlight.CoreLib.Game.Tower.TowerAwardCollection.ProgressionId(wizardId));
                session.Advanced.GetMetadataFor(session.Load<Imlight.CoreLib.Game.Tower.TowerPlayerProgression>(
                    Imlight.CoreLib.Game.Tower.TowerAwardCollection.ProgressionId(wizardId)))["@collection"] = "TowerProgressions";
                session.SaveChanges();
            }

            var type = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerEconomyCollection")
                ?? throw new InvalidOperationException("Missing TowerEconomyCollection");
            var collection = Activator.CreateInstance(type, store)
                ?? throw new InvalidOperationException("Could not create TowerEconomyCollection");

            Equal(120, Convert.ToInt32(Call(collection, "GetTicketBalance", wizardId)), "legacy Tickets migrate once into the existing native balance");
            Equal(120, Convert.ToInt32(Call(collection, "GetTicketBalance", wizardId)), "repeated balance reads cannot repeat migration");

            var forged = Call(collection, "PurchaseSpell", wizardId, 203556948U, 0);
            Equal("NotEligible", GetProperty(forged, "Status").ToString(), "unapproved spell IDs cannot be learned");
            var spellResult = Call(collection, "PurchaseSpell", wizardId, 1496157882U, 4);
            Equal("Purchased", GetProperty(spellResult, "Status").ToString(), "school spell purchase succeeds with enough Tickets");
            Equal(120, IntProperty(spellResult, "TicketsAfter"), "spell learning does not spend Arena Tickets");
            var duplicateSpell = Call(collection, "PurchaseSpell", wizardId, 1496157882U, 4);
            Check(GetProperty(duplicateSpell, "Status").ToString() is "AlreadyOwned" or "AlreadyPurchased",
                "a learned spell cannot be charged twice");
            Equal(120, IntProperty(duplicateSpell, "TicketsAfter"), "duplicate spell request preserves the Ticket balance");

            var primary = (uint[])Call(collection, "ReconcilePrimarySpells", wizardId);
            Check(primary.Contains(103007158U), "entry reconciliation grants the primary starter");
            Check(!primary.Contains(719065368U), "entry does not grant an overlevel primary attack");
            var primaryRetry = (uint[])Call(collection, "ReconcilePrimarySpells", wizardId);
            Equal(primary.Length, primaryRetry.Length, "automatic grants are idempotent");
            Equal(120, Convert.ToInt32(Call(collection, "GetTicketBalance", wizardId)), "automatic learning is free");
            var gearPurchase = Call(collection, "UpgradeItem", wizardId, ownedEquippedItem, 777U, 0, 5, 3, 0.01f);
            Equal("Purchased", GetProperty(gearPurchase, "Status").ToString(), "owned equipped item can be upgraded");
            Equal(115, IntProperty(gearPurchase, "TicketsAfter"), "gear upgrade charges the native wallet");
            Equal(1, IntProperty(gearPurchase, "UpgradeTier"), "gear upgrade advances one tier");
            var duplicateGear = Call(collection, "UpgradeItem", wizardId, ownedEquippedItem, 777U, 0, 5, 3, 0.01f);
            Check(GetProperty(duplicateGear, "Status").ToString() is "AlreadyPurchased" or "TierChanged",
                "a duplicate stale gear action cannot apply or charge twice");
            Equal(115, IntProperty(duplicateGear, "TicketsAfter"), "duplicate gear action preserves Tickets");

            var unownedGear = Call(collection, "UpgradeItem", wizardId, 515151UL, 778U, 0, 1, 3, 0.01f);
            Equal("NotOwned", GetProperty(unownedGear, "Status").ToString(), "item upgrades require a wizard-owned item ID");
            Equal(115, IntProperty(unownedGear, "TicketsAfter"), "unowned item rejection does not charge Tickets");

            using var verify = store.OpenSession();
            var wizard = verify.Load<JObject>($"tower-economy-fixtures/{wizardId}")!;
            Equal(1496157882U, wizard["SpellbookBehavior"]?["LearnedSpellTemplateIds"]?.Values<uint>().First(),
                "the learned spell persists in the native wizard spellbook");
            Equal(ownedInventoryItem, wizard["InventoryBehavior"]?["InventoryItemIds"]?.Values<ulong>().Single(),
                "item upgrade preserves the original inventory item ID");
            Equal(ownedEquippedItem, wizard["EquipmentBehavior"]?["EquippedItemIds"]?.Values<ulong>().Single(),
                "item upgrade preserves the original equipped item ID");
            Equal(1, wizard["TowerItemUpgrades"]?["ItemUpgrades"]?.Count() ?? 0,
                "per-item upgrade state persists on the wizard record");
            Equal(0, verify.Load<Imlight.CoreLib.Game.Tower.TowerPlayerProgression>(
                Imlight.CoreLib.Game.Tower.TowerAwardCollection.ProgressionId(wizardId))?.Tickets ?? -1,
                "the migrated legacy ledger cannot remain spendable");
            Equal(115, wizard["GameStats"]?["m_currentPvPCurrency"]?.Value<int>() ?? -1,
                "the native Blue Arena Ticket wallet persists equipment spending");
            Equal(20, wizard["GameStats"]?["m_currentEventCurrency1"]?.Value<int>() ?? -1,
                "native event currency remains separate from Arena spending");

            Console.WriteLine("PASS: Tower spell and owned-item purchases, atomic Ticket spending, receipts and persistence");
        }
        finally {
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(database, hardDelete: true));
        }
    }

    private static void SeedWizard(IDocumentStore store, ulong wizardId, ulong inventoryItemId, ulong equippedItemId) {
        var wizard = JObject.FromObject(new {
            CharId = wizardId,
            InventoryBehavior = new { InventoryItemIds = new[] { inventoryItemId } },
            EquipmentBehavior = new { EquippedItemIds = new[] { equippedItemId } },
            MagicSchoolBehavior = new { MagicSchool = "Fire", Level = 12 },
            SpellbookBehavior = new { LearnedSpellTemplateIds = Array.Empty<uint>() },
            GameStats = new { m_currentEventCurrency1 = 20, m_currentPvPCurrency = 100 },
        });
        using var session = store.OpenSession();
        session.Store(wizard, $"tower-economy-fixtures/{wizardId}");
        session.Advanced.GetMetadataFor(wizard)["@collection"] = "Wizards";
        session.SaveChanges();
    }

    private static object Call(object instance, string methodName, params object[] args) {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{methodName}");
        return method.Invoke(instance, args)
            ?? throw new InvalidOperationException($"{methodName} returned null");
    }

    private static object GetProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Missing {instance.GetType().Name}.{propertyName}");

    private static int IntProperty(object instance, string propertyName) => Convert.ToInt32(GetProperty(instance, propertyName));

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
