using System;
using System.Collections.Concurrent;
using System.Linq;
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
    public float DamageBonusPercent { get; set; }
}

public sealed class TowerItemUpgradeBook {
    public System.Collections.Generic.List<TowerItemUpgradeState> ItemUpgrades { get; set; } = [];
}

public sealed class TowerItemUpgradeState {
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

/// <summary>Atomically charges Tower Tickets and records a native spell or per-item upgrade.</summary>
public sealed class TowerEconomyCollection {
    private sealed class DocumentReference { public string DocumentId { get; set; } }

    private readonly IDocumentStore _store;
    private readonly ConcurrentDictionary<ulong, string> _wizardDocumentIds = new();
    private const int MaxWriteAttempts = 3;

    public TowerEconomyCollection(IDocumentStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public int GetTicketBalance(ulong wizardId) {
        if (wizardId == 0) { return 0; }
        using var session = _store.OpenSession();
        return Math.Max(0, session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId))?.Tickets ?? 0);
    }

    public TowerPurchaseResult PurchaseSpell(ulong wizardId, uint spellTemplateId, int ticketCost) {
        if (wizardId == 0 || spellTemplateId == 0 || ticketCost < 1) {
            return Result(TowerPurchaseStatus.NotEligible);
        }

        var wizardDocumentId = FindWizardDocumentId(wizardId);
        if (wizardDocumentId is null) { return Result(TowerPurchaseStatus.NotEligible); }
        var receiptId = SpellReceiptId(wizardId, spellTemplateId);

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenWriteSession();
                var existingReceipt = session.Load<TowerPurchaseReceipt>(receiptId);
                if (existingReceipt is not null) { return FromReceipt(existingReceipt, TowerPurchaseStatus.AlreadyPurchased); }

                var progression = session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId));
                var wizard = session.Load<JObject>(wizardDocumentId);
                var learnedSpells = wizard?["SpellbookBehavior"]?["LearnedSpellTemplateIds"] as JArray;
                if (progression is null || wizard?["CharId"]?.Value<ulong>() != wizardId || learnedSpells is null) {
                    return Result(TowerPurchaseStatus.NotEligible, progression?.Tickets ?? 0);
                }

                if (learnedSpells.Any(id => id.Value<uint>() == spellTemplateId)) {
                    return Result(TowerPurchaseStatus.AlreadyOwned, progression.Tickets);
                }
                if (progression.Tickets < ticketCost) {
                    return Result(TowerPurchaseStatus.InsufficientTickets, progression.Tickets);
                }

                progression.Tickets -= ticketCost;
                learnedSpells.Add(new JValue(spellTemplateId));
                SetTicketMirror(wizard, progression.Tickets);

                var receipt = new TowerPurchaseReceipt {
                    ReceiptId = receiptId,
                    WizardId = wizardId,
                    PurchaseKind = "Spell",
                    SpellTemplateId = spellTemplateId,
                    TicketsSpent = ticketCost,
                    TicketsAfter = progression.Tickets,
                };
                session.Store(receipt, receiptId);
                session.Advanced.GetMetadataFor(receipt)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerPurchaseReceipts";
                session.SaveChanges();

                return new TowerPurchaseResult {
                    Status = TowerPurchaseStatus.Purchased,
                    TicketsAfter = progression.Tickets,
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

    public TowerPurchaseResult UpgradeItem(ulong wizardId, ulong itemId, uint templateId,
        int expectedTier, int ticketCost, int maximumTier, float damageBonusPerTier) {
        if (wizardId == 0 || itemId == 0 || templateId == 0 || expectedTier < 0
            || ticketCost < 1 || maximumTier < 1 || damageBonusPerTier <= 0
            || float.IsNaN(damageBonusPerTier) || float.IsInfinity(damageBonusPerTier)) {
            return Result(TowerPurchaseStatus.NotEligible);
        }

        var wizardDocumentId = FindWizardDocumentId(wizardId);
        if (wizardDocumentId is null) { return Result(TowerPurchaseStatus.NotEligible); }
        var nextTier = expectedTier + 1;
        var receiptId = ItemReceiptId(wizardId, itemId, nextTier);

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenWriteSession();
                var existingReceipt = session.Load<TowerPurchaseReceipt>(receiptId);
                if (existingReceipt is not null) { return FromReceipt(existingReceipt, TowerPurchaseStatus.AlreadyPurchased); }

                var progression = session.Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(wizardId));
                var wizard = session.Load<JObject>(wizardDocumentId);
                if (progression is null || wizard?["CharId"]?.Value<ulong>() != wizardId) {
                    return Result(TowerPurchaseStatus.NotEligible, progression?.Tickets ?? 0);
                }
                if (!OwnsItem(wizard, itemId)) { return Result(TowerPurchaseStatus.NotOwned, progression.Tickets); }

                var upgradeBook = wizard["TowerItemUpgrades"] as JObject ?? new JObject();
                var itemUpgrades = upgradeBook["ItemUpgrades"] as JArray ?? new JArray();
                upgradeBook["ItemUpgrades"] = itemUpgrades;
                wizard["TowerItemUpgrades"] = upgradeBook;
                var prior = itemUpgrades.FirstOrDefault(entry => entry?["ItemId"]?.Value<ulong>() == itemId) as JObject;
                var currentTier = prior?["Tier"]?.Value<int>() ?? 0;
                if (prior is not null && prior["TemplateId"]?.Value<uint>() != templateId) {
                    return Result(TowerPurchaseStatus.NotOwned, progression.Tickets);
                }
                if (currentTier != expectedTier) { return Result(TowerPurchaseStatus.TierChanged, progression.Tickets, currentTier, prior?["DamageBonusPercent"]?.Value<float>() ?? 0); }
                if (currentTier >= maximumTier) { return Result(TowerPurchaseStatus.MaximumTierReached, progression.Tickets, currentTier, prior?["DamageBonusPercent"]?.Value<float>() ?? 0); }
                if (progression.Tickets < ticketCost) { return Result(TowerPurchaseStatus.InsufficientTickets, progression.Tickets, currentTier, prior?["DamageBonusPercent"]?.Value<float>() ?? 0); }

                var upgraded = new TowerItemUpgradeState {
                    ItemId = itemId,
                    TemplateId = templateId,
                    Tier = nextTier,
                    DamageBonusPercent = (prior?["DamageBonusPercent"]?.Value<float>() ?? 0) + damageBonusPerTier,
                    UpdatedUtc = DateTime.UtcNow,
                };
                if (prior is null) { itemUpgrades.Add(JObject.FromObject(upgraded)); }
                else { prior.Replace(JObject.FromObject(upgraded)); }

                progression.Tickets -= ticketCost;
                SetTicketMirror(wizard, progression.Tickets);
                var receipt = new TowerPurchaseReceipt {
                    ReceiptId = receiptId,
                    WizardId = wizardId,
                    PurchaseKind = "ItemUpgrade",
                    ItemId = itemId,
                    UpgradeTier = nextTier,
                    TicketsSpent = ticketCost,
                    TicketsAfter = progression.Tickets,
                    DamageBonusPercent = upgraded.DamageBonusPercent,
                };
                session.Store(receipt, receiptId);
                session.Advanced.GetMetadataFor(receipt)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerPurchaseReceipts";
                session.SaveChanges();

                return new TowerPurchaseResult {
                    Status = TowerPurchaseStatus.Purchased,
                    TicketsAfter = progression.Tickets,
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

    private static void SetTicketMirror(JObject wizard, int balance) {
        if (wizard["GameStats"] is JObject stats) { stats["m_currentEventCurrency1"] = balance; }
    }

    private static TowerPurchaseResult FromReceipt(TowerPurchaseReceipt receipt, TowerPurchaseStatus status) => new() {
        Status = status,
        TicketsAfter = receipt.TicketsAfter,
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
