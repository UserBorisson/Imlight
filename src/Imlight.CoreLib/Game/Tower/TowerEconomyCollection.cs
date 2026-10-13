using System;
using System.Collections.Concurrent;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Behaviors;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Imlight.CoreLib.Game.Tower;

public enum TowerPurchaseStatus {
    Purchased,
    AlreadyOwned,
    AlreadyPurchased,
    InsufficientTickets,
    NotOwned,
    TierChanged,
    MaximumTierReached,
    NotEligible,
    RetryableFailure,
}

public sealed class TowerPurchaseResult {
    public TowerPurchaseStatus Status { get; set; }
    public int TicketsAfter { get; set; }
    public int UpgradeTier { get; set; }
    public TowerItemUpgradeState Upgrade { get; set; }
    public float DamageBonusPercent { get; set; }
}

public sealed class TowerItemUpgradeBook {
    public System.Collections.Generic.List<TowerItemUpgradeState> ItemUpgrades { get; set; } = [];
}

public sealed class TowerItemUpgradeState {
    public int SchemaVersion { get; set; }
    public string UpgradeSchool { get; set; }
    public TowerItemStats Stats { get; set; } = TowerItemStats.Zero;
    public int LegacyCreditUsed { get; set; }

    public ulong ItemId { get; set; }
    public uint TemplateId { get; set; }
    public int Tier { get; set; }
    public float DamageBonusPercent { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TowerPurchaseReceipt {
    public string ReceiptId { get; set; } = string.Empty;
    public ulong WizardId { get; set; }
    public string PurchaseKind { get; set; } = string.Empty;
    public uint SpellTemplateId { get; set; }
    public ulong ItemId { get; set; }
    public int UpgradeTier { get; set; }
    public int TicketsSpent { get; set; }
    public int TicketsAfter { get; set; }
    public float DamageBonusPercent { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Atomically records native learning and spends Arena Tickets for per-item upgrades.</summary>
public sealed class TowerEconomyCollection {
    private sealed class DocumentReference { public string DocumentId { get; set; } }

    private readonly IDocumentStore _store;
    private readonly ConcurrentDictionary<ulong, string> _wizardDocumentIds = new();
    private const int MaxWriteAttempts = 3;

    public TowerEconomyCollection(IDocumentStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public int GetTicketBalance(ulong wizardId) {
        if (wizardId == 0) { return 0; }
        var wizardDocumentId = FindWizardDocumentId(wizardId);
        if (wizardDocumentId is null) { return 0; }
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenWriteSession();
                var wizard = session.Load<JObject>(wizardDocumentId);
                if (wizard?["CharId"]?.Value<ulong>() != wizardId) { return 0; }
                var progressionId = TowerAwardCollection.ProgressionId(wizardId);
                var progression = session.Load<TowerPlayerProgression>(progressionId);
                if (progression is null) {
                    progression = new TowerPlayerProgression { WizardId = wizardId };
                    session.Store(progression, progressionId);
                    session.Advanced.GetMetadataFor(progression)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerProgressions";
                }
                var balance = TowerArenaWallet.Migrate(wizard, progression);
                progression.GetPermanentClears();
                session.SaveChanges();
                return balance;
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) { }
        }
        throw new InvalidOperationException("Native Arena wallet changed concurrently; retry the request.");
    }

    public TowerClearHistory GetPermanentClearHistory(ulong wizardId) {
        _ = GetTicketBalance(wizardId);
        using var session = _store.OpenSession();
        return session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId))?.GetPermanentClears() ?? new TowerClearHistory();
    }

    // Returns authoritative eligible primary IDs as well as committing missing grants.
    // Returning the full eligible set also repairs a live session after a dropped notification.
    public uint[] ReconcilePrimarySpells(ulong wizardId) {
        var documentId = FindWizardDocumentId(wizardId);
        if (documentId is null) return [];
        _ = GetTicketBalance(wizardId);
        for (var attempt = 0; ; attempt++) {
            using var session = OpenWriteSession();
            var wizard = session.Load<JObject>(documentId);
            if (wizard?["CharId"]?.Value<ulong>() != wizardId) return [];
            var progression = session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId));
            var learned = wizard["SpellbookBehavior"]?["LearnedSpellTemplateIds"] as JArray;
            if (learned is null || progression is null) return [];
            var school = wizard["MagicSchoolBehavior"]?["MagicSchool"]?.ToObject<MagicSchool>().ToString();
            var level = wizard["MagicSchoolBehavior"]?["Level"]?.Value<int>() ?? 1;
            var eligible = TowerSpellProgression.Spells.Where(s => TowerSpellProgression.EligiblePrimary(
                s, school, level, progression.GetPermanentClears())).Select(s => s.TemplateId).ToArray();
            var existing = learned.Values<uint>().ToHashSet();
            foreach (var id in eligible) if (existing.Add(id)) learned.Add(id);
            try { session.SaveChanges(); return eligible; }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) { }
        }
    }

    public TowerPurchaseResult PurchaseSpell(ulong wizardId, uint spellTemplateId, int ticketCost) {
        if (wizardId == 0 || spellTemplateId == 0 || ticketCost < 0) {
            return Result(TowerPurchaseStatus.NotEligible);
        }

        var wizardDocumentId = FindWizardDocumentId(wizardId);
        if (wizardDocumentId is null) { return Result(TowerPurchaseStatus.NotEligible); }
        _ = GetTicketBalance(wizardId);
        var receiptId = SpellReceiptId(wizardId, spellTemplateId);

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenWriteSession();
                var existingReceipt = session.Load<TowerPurchaseReceipt>(receiptId);
                if (existingReceipt is not null) { return FromReceipt(existingReceipt, TowerPurchaseStatus.AlreadyPurchased, GetTicketBalance(wizardId)); }

                var progression = session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId));
                var wizard = session.Load<JObject>(wizardDocumentId);
                var balance = TowerArenaWallet.GetBalance(wizard);
                var learnedSpells = wizard?["SpellbookBehavior"]?["LearnedSpellTemplateIds"] as JArray;
                if (progression is null || wizard?["CharId"]?.Value<ulong>() != wizardId || learnedSpells is null) {
                    return Result(TowerPurchaseStatus.NotEligible, balance);
                }

                if (learnedSpells.Any(id => id.Value<uint>() == spellTemplateId)) {
                    return Result(TowerPurchaseStatus.AlreadyOwned, balance);
                }
                var unlock = TowerSpellProgression.Find(spellTemplateId);
                var school = wizard["MagicSchoolBehavior"]?["MagicSchool"]?.ToObject<MagicSchool>().ToString();
                var level = wizard["MagicSchoolBehavior"]?["Level"]?.Value<int>() ?? 1;
                if (!TowerSpellProgression.EligibleSecondary(unlock, school, level, learnedSpells.Values<uint>()))
                    return Result(TowerPurchaseStatus.NotEligible, balance);
                learnedSpells.Add(new JValue(spellTemplateId));

                var receipt = new TowerPurchaseReceipt {
                    ReceiptId = receiptId,
                    WizardId = wizardId,
                    PurchaseKind = "Spell",
                    SpellTemplateId = spellTemplateId,
                    TicketsSpent = 0,
                    TicketsAfter = balance,
                };
                session.Store(receipt, receiptId);
                session.Advanced.GetMetadataFor(receipt)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerPurchaseReceipts";
                session.SaveChanges();

                return new TowerPurchaseResult {
                    Status = TowerPurchaseStatus.Purchased,
                    TicketsAfter = balance,
                };
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) {
                // Re-read the balance, spellbook and receipt after a concurrent purchase.
            }
            catch (Exception) {
                return Result(TowerPurchaseStatus.RetryableFailure);
            }
        }

        return Result(TowerPurchaseStatus.RetryableFailure);
    }

    public TowerItemUpgradePreview GetEquippedUpgradePreview(ulong wizardId, ulong itemId) {
        var documentId = FindWizardDocumentId(wizardId);
        if (documentId is null) return null;
        _ = GetTicketBalance(wizardId);
        using var session = OpenWriteSession();
        return ReadUpgradePreview(session, wizardId, itemId, session.Load<JObject>(documentId));
    }

    public TowerPurchaseResult UpgradeEquippedItem(ulong wizardId, ulong itemId, uint templateId,
        int expectedTier, int expectedCost) {
        var documentId = FindWizardDocumentId(wizardId);
        if (documentId is null) return Result(TowerPurchaseStatus.NotEligible);
        _ = GetTicketBalance(wizardId);
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenWriteSession();
                var wizard = session.Load<JObject>(documentId);
                var balance = TowerArenaWallet.GetBalance(wizard);
                var preview = ReadUpgradePreview(session, wizardId, itemId, wizard, templateId);
                if (preview is null) return Result(TowerPurchaseStatus.NotOwned, balance);
                if (preview.CurrentTier != expectedTier || preview.Cost != expectedCost)
                    return Result(TowerPurchaseStatus.TierChanged, balance, preview.CurrentTier);
                if (!preview.Eligible) return Result(TowerPurchaseStatus.NotEligible, balance, preview.CurrentTier);
                var receiptId = ItemReceiptId(wizardId, itemId, preview.DestinationTier) + "/v2";
                if (session.Load<TowerPurchaseReceipt>(receiptId) is not null)
                    return Result(TowerPurchaseStatus.AlreadyPurchased, balance, preview.CurrentTier);
                if (!TowerArenaWallet.TrySpend(wizard, preview.Cost, out balance))
                    return Result(TowerPurchaseStatus.InsufficientTickets, balance, preview.CurrentTier);
                var book = wizard["TowerItemUpgrades"] as JObject ?? new JObject();
                var entries = book["ItemUpgrades"] as JArray ?? new JArray();
                book["ItemUpgrades"] = entries;
                wizard["TowerItemUpgrades"] = book;
                var prior = entries.FirstOrDefault(e => e["ItemId"]?.Value<ulong>() == itemId);
                var upgrade = new TowerItemUpgradeState {
                    SchemaVersion = 2, ItemId = itemId, TemplateId = templateId,
                    UpgradeSchool = preview.School, Tier = preview.DestinationTier,
                    Stats = preview.UpgradeStats, LegacyCreditUsed = preview.LegacyCreditUsed,
                    DamageBonusPercent = prior?["DamageBonusPercent"]?.Value<float>() ?? 0,
                };
                if (prior is null) entries.Add(JObject.FromObject(upgrade));
                else prior.Replace(JObject.FromObject(upgrade));
                var receipt = new TowerPurchaseReceipt {
                    ReceiptId=receiptId, WizardId=wizardId, PurchaseKind="ItemUpgradeV2", ItemId=itemId,
                    UpgradeTier=upgrade.Tier, TicketsSpent=preview.Cost, TicketsAfter=balance,
                };
                session.Store(receipt, receiptId);
                session.Advanced.GetMetadataFor(receipt)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerPurchaseReceipts";
                session.SaveChanges();
                return new TowerPurchaseResult {Status=TowerPurchaseStatus.Purchased, TicketsAfter=balance,
                    UpgradeTier=upgrade.Tier, Upgrade=upgrade, DamageBonusPercent=upgrade.DamageBonusPercent};
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) { }
            catch (Exception) { return Result(TowerPurchaseStatus.RetryableFailure); }
        }
        return Result(TowerPurchaseStatus.RetryableFailure);
    }

    private static TowerItemUpgradePreview ReadUpgradePreview(IDocumentSession session, ulong wizardId,
        ulong itemId, JObject wizard, uint expectedTemplateId = 0) {
        if (wizard?["CharId"]?.Value<ulong>() != wizardId
            || (wizard["EquipmentBehavior"]?["EquippedItemIds"] as JArray)?.Values<ulong>().Contains(itemId) != true)
            return null;
        var item = session.Advanced.RawQuery<WizClientObjectItem>("from WizardItems where m_globalID = $itemId")
            .AddParameter("itemId", itemId).WaitForNonStaleResults(TimeSpan.FromSeconds(5)).SingleOrDefault();
        if (item is null || (expectedTemplateId != 0 && item.m_templateID != expectedTemplateId)) return null;
        var template = CoreObjectFactory.GetCoreTemplate(item.m_templateID) as WizItemTemplate;
        if (template is null) return null;
        var progression = session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId));
        if (progression is null) return null;
        var prior = (wizard["TowerItemUpgrades"]?["ItemUpgrades"] as JArray)?
            .FirstOrDefault(e => e["ItemId"]?.Value<ulong>() == itemId)?.ToObject<TowerItemUpgradeState>();
        if (prior is not null && prior.TemplateId != item.m_templateID) return null;
        var legacySpent = session.Advanced.RawQuery<TowerPurchaseReceipt>(
                "from TowerPurchaseReceipts where WizardId = $wizard and ItemId = $item and PurchaseKind = 'ItemUpgrade'")
            .AddParameter("wizard", wizardId).AddParameter("item", itemId)
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5)).ToList().Sum(r => r.TicketsSpent);
        var preview = TowerItemUpgradeAdapter.Preview(template,
            wizard["MagicSchoolBehavior"]?["MagicSchool"]?.ToObject<MagicSchool>().ToString(),
            wizard["MagicSchoolBehavior"]?["Level"]?.Value<int>() ?? 1,
            progression.GetPermanentClears(), prior, legacySpent);
        if (preview.Eligible) {
            _ = TowerItemUpgradeAdapter.Effects(new TowerItemUpgradeState {SchemaVersion=2, UpgradeSchool=preview.School,
                Stats=preview.UpgradeStats, DamageBonusPercent=prior?.DamageBonusPercent ?? 0}).ToArray();
        }
        return preview;
    }

    public TowerPurchaseResult UpgradeItem(ulong wizardId, ulong itemId, uint templateId,
        int expectedTier, int ticketCost, int maximumTier, float damageBonusPerTier) {
        if (wizardId == 0 || itemId == 0 || templateId == 0 || expectedTier < 0
            || ticketCost < 1 || maximumTier < 1 || damageBonusPerTier <= 0
            || float.IsNaN(damageBonusPerTier) || float.IsInfinity(damageBonusPerTier)) {
            return Result(TowerPurchaseStatus.NotEligible);
        }

        var wizardDocumentId = FindWizardDocumentId(wizardId);
        if (wizardDocumentId is null) { return Result(TowerPurchaseStatus.NotEligible); }
        _ = GetTicketBalance(wizardId);
        var nextTier = expectedTier + 1;
        var receiptId = ItemReceiptId(wizardId, itemId, nextTier);

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenWriteSession();
                var existingReceipt = session.Load<TowerPurchaseReceipt>(receiptId);
                if (existingReceipt is not null) { return FromReceipt(existingReceipt, TowerPurchaseStatus.AlreadyPurchased, GetTicketBalance(wizardId)); }

                var progression = session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId));
                var wizard = session.Load<JObject>(wizardDocumentId);
                var balance = TowerArenaWallet.GetBalance(wizard);
                if (progression is null || wizard?["CharId"]?.Value<ulong>() != wizardId) {
                    return Result(TowerPurchaseStatus.NotEligible, balance);
                }
                if (!OwnsItem(wizard, itemId)) { return Result(TowerPurchaseStatus.NotOwned, balance); }

                var upgradeBook = wizard["TowerItemUpgrades"] as JObject ?? new JObject();
                var itemUpgrades = upgradeBook["ItemUpgrades"] as JArray ?? new JArray();
                upgradeBook["ItemUpgrades"] = itemUpgrades;
                wizard["TowerItemUpgrades"] = upgradeBook;
                var prior = itemUpgrades.FirstOrDefault(entry => entry?["ItemId"]?.Value<ulong>() == itemId) as JObject;
                var currentTier = prior?["Tier"]?.Value<int>() ?? 0;
                if (prior is not null && prior["TemplateId"]?.Value<uint>() != templateId) {
                    return Result(TowerPurchaseStatus.NotOwned, balance);
                }
                if (currentTier != expectedTier) { return Result(TowerPurchaseStatus.TierChanged, balance, currentTier, prior?["DamageBonusPercent"]?.Value<float>() ?? 0); }
                if (currentTier >= maximumTier) { return Result(TowerPurchaseStatus.MaximumTierReached, balance, currentTier, prior?["DamageBonusPercent"]?.Value<float>() ?? 0); }
                if (balance < ticketCost) { return Result(TowerPurchaseStatus.InsufficientTickets, balance, currentTier, prior?["DamageBonusPercent"]?.Value<float>() ?? 0); }

                var upgraded = new TowerItemUpgradeState {
                    ItemId = itemId,
                    TemplateId = templateId,
                    Tier = nextTier,
                    DamageBonusPercent = (prior?["DamageBonusPercent"]?.Value<float>() ?? 0) + damageBonusPerTier,
                    UpdatedUtc = DateTime.UtcNow,
                };
                if (prior is null) { itemUpgrades.Add(JObject.FromObject(upgraded)); }
                else { prior.Replace(JObject.FromObject(upgraded)); }

                if (!TowerArenaWallet.TrySpend(wizard, ticketCost, out balance)) {
                    return Result(TowerPurchaseStatus.InsufficientTickets, balance);
                }
                var receipt = new TowerPurchaseReceipt {
                    ReceiptId = receiptId,
                    WizardId = wizardId,
                    PurchaseKind = "ItemUpgrade",
                    ItemId = itemId,
                    UpgradeTier = nextTier,
                    TicketsSpent = ticketCost,
                    TicketsAfter = balance,
                    DamageBonusPercent = upgraded.DamageBonusPercent,
                };
                session.Store(receipt, receiptId);
                session.Advanced.GetMetadataFor(receipt)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerPurchaseReceipts";
                session.SaveChanges();

                return new TowerPurchaseResult {
                    Status = TowerPurchaseStatus.Purchased,
                    TicketsAfter = balance,
                    UpgradeTier = upgraded.Tier,
                    DamageBonusPercent = upgraded.DamageBonusPercent,
                };
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) {
                // Re-read the item tier, balance and idempotency receipt after a concurrent purchase.
            }
            catch (Exception) {
                return Result(TowerPurchaseStatus.RetryableFailure);
            }
        }

        return Result(TowerPurchaseStatus.RetryableFailure);
    }

    public static string SpellReceiptId(ulong wizardId, uint spellTemplateId)
        => $"tower-purchase-receipts/{wizardId}/spell/{spellTemplateId}";

    public static string ItemReceiptId(ulong wizardId, ulong itemId, int tier)
        => $"tower-purchase-receipts/{wizardId}/item/{itemId}/tier-{tier}";

    private IDocumentSession OpenWriteSession() {
        var session = _store.OpenSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return session;
    }

    private string FindWizardDocumentId(ulong wizardId) {
        if (_wizardDocumentIds.TryGetValue(wizardId, out var cached)) { return cached; }
        using var session = _store.OpenSession();
        var reference = session.Advanced.RawQuery<DocumentReference>(
                "from Wizards where CharId = $wizardId select id() as DocumentId")
            .AddParameter("wizardId", wizardId)
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .SingleOrDefault();
        if (string.IsNullOrWhiteSpace(reference?.DocumentId)) { return null; }
        return _wizardDocumentIds.GetOrAdd(wizardId, reference.DocumentId);
    }

    private static bool OwnsItem(JObject wizard, ulong itemId) {
        var inventory = wizard["InventoryBehavior"]?["InventoryItemIds"] as JArray;
        var equipped = wizard["EquipmentBehavior"]?["EquippedItemIds"] as JArray;
        return (inventory?.Any(id => id.Value<ulong>() == itemId) ?? false)
            || (equipped?.Any(id => id.Value<ulong>() == itemId) ?? false);
    }

    private static TowerPurchaseResult FromReceipt(TowerPurchaseReceipt receipt, TowerPurchaseStatus status, int currentBalance) => new() {
        Status = status,
        TicketsAfter = currentBalance,
        UpgradeTier = receipt.UpgradeTier,
        DamageBonusPercent = receipt.DamageBonusPercent,
    };

    private static TowerPurchaseResult Result(TowerPurchaseStatus status, int tickets = 0,
        int tier = 0, float damageBonusPercent = 0) => new() {
        Status = status,
        TicketsAfter = tickets,
        UpgradeTier = tier,
        DamageBonusPercent = damageBonusPercent,
    };
}
