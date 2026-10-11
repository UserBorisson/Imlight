using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace Imlight.CoreLib.Game.Tower;

public enum TowerAwardStatus {
    Awarded,
    AlreadyAwarded,
    NotEligible,
    InventoryFull,
    RetryableFailure,
}

public sealed class TowerAwardRequest {
    public string AwardId { get; set; } = string.Empty;
    public ulong WizardId { get; set; }
    public int ExperiencePoints { get; set; }
    public int HighestFloorCleared { get; set; }
    public uint[] ItemTemplateIds { get; set; } = [];
}

public sealed class TowerAwardResult {
    public TowerAwardStatus Status { get; set; }
    public List<ulong> ItemIds { get; set; } = [];
    public List<WizClientObjectItem> Items { get; set; } = [];
    public int ExperiencePointsAwarded { get; set; }
    public int ExperiencePointsAfter { get; set; }
    public int LevelAfter { get; set; }
    public int HighestFloorCleared { get; set; }
}

public sealed class TowerAwardReceipt {
    public string AwardId { get; set; } = string.Empty;
    public ulong WizardId { get; set; }
    public int ExperiencePointsAwarded { get; set; }
    public int ExperiencePointsAfter { get; set; }
    public int LevelAfter { get; set; }
    public int HighestFloorCleared { get; set; }
    public List<ulong> ItemIds { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TowerPlayerProgression {
    public ulong WizardId { get; set; }
    public int HighestFloorCleared { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Commits the wizard XP, inventory references, item documents, persistent floor progression,
/// and idempotency receipt in a single RavenDB transaction.
/// </summary>
public sealed class TowerAwardCollection(IDocumentStore store, int capacity, Func<uint, WizClientObjectItem> createItem) {
    private sealed class DocumentReference { public string DocumentId { get; set; } }

    private readonly IDocumentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Func<uint, WizClientObjectItem> _createItem = createItem ?? throw new ArgumentNullException(nameof(createItem));
    private const int MaxWriteAttempts = 3;

    public TowerAwardResult Commit(TowerAwardRequest request) {
        if (request is null || string.IsNullOrWhiteSpace(request.AwardId) || request.WizardId == 0
            || request.ExperiencePoints < 0 || request.HighestFloorCleared is < 0 or > TowerRunState.FloorCount
            || request.ItemTemplateIds is null || request.ItemTemplateIds.Any(id => id == 0)) {
            return Result(TowerAwardStatus.NotEligible);
        }

        var receiptId = ReceiptId(request.WizardId, request.AwardId);
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = _store.OpenSession();
                session.Advanced.UseOptimisticConcurrency = true;

                var existing = session.Load<TowerAwardReceipt>(receiptId);
                if (existing is not null) {
                    var priorItems = existing.ItemIds
                        .Select(itemId => session.Load<WizClientObjectItem>(ItemId(request.WizardId, itemId)))
                        .Where(item => item is not null)
                        .ToList();
                    return new TowerAwardResult {
                        Status = TowerAwardStatus.AlreadyAwarded,
                        ItemIds = [.. existing.ItemIds],
                        Items = priorItems,
                        ExperiencePointsAwarded = existing.ExperiencePointsAwarded,
                        ExperiencePointsAfter = existing.ExperiencePointsAfter,
                        LevelAfter = existing.LevelAfter,
                        HighestFloorCleared = existing.HighestFloorCleared,
                    };
                }

                var wizardReference = session.Advanced.RawQuery<DocumentReference>(
                        "from Wizards where CharId = $wizardId select id() as DocumentId")
                    .AddParameter("wizardId", request.WizardId)
                    .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
                    .SingleOrDefault();
                if (wizardReference is null) { return Result(TowerAwardStatus.NotEligible); }

                var wizard = session.Load<JObject>(wizardReference.DocumentId);
                if (wizard is null || wizard["CharId"]?.Value<ulong>() != request.WizardId) {
                    return Result(TowerAwardStatus.NotEligible);
                }

                if (wizard["InventoryBehavior"]?[nameof(Wizard.InventoryBehavior.InventoryItemIds)] is not JArray inventoryIds) {
                    return Result(TowerAwardStatus.NotEligible);
                }

                var items = new List<WizClientObjectItem>(request.ItemTemplateIds.Length);
                foreach (var templateId in request.ItemTemplateIds) {
                    var item = _createItem(templateId);
                    if (item is null || item.m_globalID == 0 || item.m_templateID != templateId) {
                        return Result(TowerAwardStatus.RetryableFailure);
                    }
                    item.m_characterId = request.WizardId;
                    items.Add(item);
                }

                if (items.Select(item => item.m_globalID).Distinct().Count() != items.Count) {
                    return Result(TowerAwardStatus.RetryableFailure);
                }

                var equippedIds = wizard["EquipmentBehavior"]?["EquippedItemIds"] as JArray ?? new JArray();
                var occupiedIds = inventoryIds.Select(id => id.Value<ulong>())
                    .Concat(equippedIds.Select(id => id.Value<ulong>()))
                    .Distinct()
                    .Count();
                if (capacity - occupiedIds < items.Count) { return Result(TowerAwardStatus.InventoryFull); }

                var magicSchoolBehavior = wizard["MagicSchoolBehavior"] as JObject;
                var currentXp = magicSchoolBehavior?["ExperiencePoints"]?.Value<int>();
                if (magicSchoolBehavior is null || currentXp is null) { return Result(TowerAwardStatus.NotEligible); }
                var experienceAfter = checked(currentXp.Value + request.ExperiencePoints);
                var liveWizard = wizard.ToObject<Wizard>();
                if (liveWizard?.MagicSchoolBehavior is null || liveWizard.GameStats is null) {
                    return Result(TowerAwardStatus.NotEligible);
                }
                var levelAfter = liveWizard.ApplyCommittedExperiencePoints(experienceAfter);
                magicSchoolBehavior["ExperiencePoints"] = liveWizard.MagicSchoolBehavior.ExperiencePoints;
                magicSchoolBehavior["Level"] = liveWizard.MagicSchoolBehavior.Level;
                if (wizard["GameStats"] is JObject gameStats) {
                    gameStats["m_baseHitpoints"] = liveWizard.GameStats.m_baseHitpoints;
                    gameStats["m_baseMana"] = liveWizard.GameStats.m_baseMana;
                }

                foreach (var item in items) {
                    var itemId = (ulong)item.m_globalID;
                    inventoryIds.Add(new JValue(itemId));
                    var itemDocumentId = ItemId(request.WizardId, itemId);
                    session.Store(item, itemDocumentId);
                    session.Advanced.GetMetadataFor(item)[Raven.Client.Constants.Documents.Metadata.Collection] = "WizardItems";
                }

                var progressionId = ProgressionId(request.WizardId);
                var progression = session.Load<TowerPlayerProgression>(progressionId);
                var createProgression = progression is null;
                progression ??= new TowerPlayerProgression { WizardId = request.WizardId };
                progression.HighestFloorCleared = Math.Max(progression.HighestFloorCleared, request.HighestFloorCleared);
                progression.UpdatedUtc = DateTime.UtcNow;
                if (createProgression) {
                    session.Store(progression, progressionId);
                    session.Advanced.GetMetadataFor(progression)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerProgressions";
                }

                var receipt = new TowerAwardReceipt {
                    AwardId = request.AwardId,
                    WizardId = request.WizardId,
                    ExperiencePointsAwarded = request.ExperiencePoints,
                    ExperiencePointsAfter = experienceAfter,
                    LevelAfter = levelAfter,
                    HighestFloorCleared = progression.HighestFloorCleared,
                    ItemIds = [.. items.Select(item => (ulong)item.m_globalID)],
                };
                session.Store(receipt, receiptId);
                session.Advanced.GetMetadataFor(receipt)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerAwardReceipts";
                session.SaveChanges();

                return new TowerAwardResult {
                    Status = TowerAwardStatus.Awarded,
                    ItemIds = [.. receipt.ItemIds],
                    Items = items,
                    ExperiencePointsAwarded = request.ExperiencePoints,
                    ExperiencePointsAfter = experienceAfter,
                    LevelAfter = levelAfter,
                    HighestFloorCleared = progression.HighestFloorCleared,
                };
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) {
                // If another recipient transaction won the race, the next pass returns its receipt.
            }
            catch (Exception) {
                return Result(TowerAwardStatus.RetryableFailure);
            }
        }

        return Result(TowerAwardStatus.RetryableFailure);
    }

    public static string ReceiptId(ulong wizardId, string awardId)
        => $"tower-awards/{wizardId}/{Uri.EscapeDataString(awardId)}";

    public static string ProgressionId(ulong wizardId) => $"tower-progressions/{wizardId}";
    public static string ItemId(ulong wizardId, ulong itemId) => $"tower-award-items/{wizardId}/{itemId}";

    private static TowerAwardResult Result(TowerAwardStatus status) => new() { Status = status };
}
