using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
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
    public string RunId { get; set; } = string.Empty;
    public string AwardId { get; set; } = string.Empty;
    public ulong WizardId { get; set; }
    public int ExperiencePoints { get; set; }
    public long Floor { get; set; }
    public int Tickets { get; set; }
    public long HighestFloorCleared { get; set; }
    public uint[] ItemTemplateIds { get; set; } = [];
}

public sealed class TowerAwardResult {
    public TowerAwardStatus Status { get; set; }
    public List<ulong> ItemIds { get; set; } = [];
    public List<WizClientObjectItem> Items { get; set; } = [];
    public int ExperiencePointsAwarded { get; set; }
    public int ExperiencePointsAfter { get; set; }
    public int LevelAfter { get; set; }
    public int LevelsAwarded { get; set; }
    public int TicketsAwarded { get; set; }
    public int TicketsAfter { get; set; }
    public long HighestFloorCleared { get; set; }
}

public sealed class TowerAwardReceipt {
    public string AwardId { get; set; } = string.Empty;
    public ulong WizardId { get; set; }
    public int ExperiencePointsAwarded { get; set; }
    public int ExperiencePointsAfter { get; set; }
    public int LevelAfter { get; set; }
    public int LevelsAwarded { get; set; }
    public int TicketsAwarded { get; set; }
    public int TicketsAfter { get; set; }
    public long HighestFloorCleared { get; set; }
    public List<ulong> ItemIds { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TowerPlayerProgression {
    public ulong WizardId { get; set; }
    public long HighestFloorCleared { get; set; }
    public List<long> ClearedFloors { get; set; } = [];
    public int Tickets { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Commits the wizard's first-clear level, Tower Tickets, inventory references, item documents,
/// persistent floor progression, and idempotency receipt in one RavenDB transaction.
/// </summary>
public sealed class TowerAwardCollection {
    private sealed class DocumentReference { public string DocumentId { get; set; } }

    private readonly IDocumentStore _store;
    private readonly Func<uint, WizClientObjectItem> _createItem;
    private readonly int _capacity;
    private readonly TowerServerOwnership _ownership;
    private readonly Func<int> _getMaximumLevel;
    private readonly Func<int, int> _getExperienceAtLevel;
    private readonly Action<Wizard, byte> _setLevel;
    private readonly ConcurrentDictionary<ulong, string> _wizardDocumentIds = new();
    private const int MaxWriteAttempts = 3;

    public TowerAwardCollection(IDocumentStore store, int capacity, Func<uint, WizClientObjectItem> createItem)
        : this(store, capacity, createItem, TowerServerOwnership.CurrentProcessOwner) { }

    internal TowerAwardCollection(IDocumentStore store, int capacity, Func<uint, WizClientObjectItem> createItem,
                                  TowerServerOwnership ownership)
        : this(store, capacity, createItem, ownership,
            () => {
                _ = MagicLevelsConfig.Instance;
                return MagicLevelsConfig.MaxLevel;
            },
            level => {
                _ = MagicLevelsConfig.Instance;
                return MagicLevelsConfig.GetExperiencePointsAtLevel(level);
            },
            (wizard, level) => wizard.SetLevelWithoutPersistence(level)) { }

    internal TowerAwardCollection(IDocumentStore store, int capacity, Func<uint, WizClientObjectItem> createItem,
                                  TowerServerOwnership ownership, Func<int> getMaximumLevel,
                                  Func<int, int> getExperienceAtLevel, Action<Wizard, byte> setLevel) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _capacity = capacity;
        _createItem = createItem ?? throw new ArgumentNullException(nameof(createItem));
        _ownership = ownership;
        _getMaximumLevel = getMaximumLevel ?? throw new ArgumentNullException(nameof(getMaximumLevel));
        _getExperienceAtLevel = getExperienceAtLevel ?? throw new ArgumentNullException(nameof(getExperienceAtLevel));
        _setLevel = setLevel ?? throw new ArgumentNullException(nameof(setLevel));
    }

    internal TowerAwardCollection(IDocumentStore store, int capacity, Func<uint, WizClientObjectItem> createItem,
                                  Func<int> getMaximumLevel, Func<int, int> getExperienceAtLevel,
                                  Action<Wizard, byte> setLevel)
        : this(store, capacity, createItem, null, getMaximumLevel, getExperienceAtLevel, setLevel) { }

    public TowerAwardResult Commit(TowerAwardRequest request) {
        if (request is null || string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.AwardId) || request.WizardId == 0
            || request.ExperiencePoints < 0 || request.Floor < 1 || request.Tickets < 0
            || request.HighestFloorCleared < request.Floor
            || request.ItemTemplateIds is null || request.ItemTemplateIds.Any(id => id == 0)) {
            return Result(TowerAwardStatus.NotEligible);
        }

        var receiptId = ReceiptId(request.WizardId, request.AwardId);
        var wizardDocumentId = FindWizardDocumentId(request.WizardId);
        if (wizardDocumentId is null) { return Result(TowerAwardStatus.NotEligible); }
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenWriteSession();

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
                        LevelsAwarded = existing.LevelsAwarded,
                        TicketsAwarded = existing.TicketsAwarded,
                        TicketsAfter = existing.TicketsAfter,
                        HighestFloorCleared = existing.HighestFloorCleared,
                    };
                }

                var run = session.Load<TowerRunState>(RunDocumentId(request.RunId));
                var pendingAward = (run?.PendingAwards ?? []).FirstOrDefault(award =>
                    string.Equals(award.AwardId, request.AwardId, StringComparison.Ordinal));
                if (pendingAward is null
                    || !string.Equals(pendingAward.RunId, request.RunId, StringComparison.Ordinal)
                    || pendingAward.WizardId != request.WizardId
                    || pendingAward.ExperiencePoints != request.ExperiencePoints
                    || pendingAward.Floor != request.Floor
                    || pendingAward.Tickets != request.Tickets
                    || pendingAward.HighestFloorCleared != request.HighestFloorCleared
                    || !(pendingAward.ItemTemplateIds ?? []).SequenceEqual(request.ItemTemplateIds)) {
                    return Result(TowerAwardStatus.NotEligible);
                }

                if (_ownership is not null && !_ownership.Touch(session)) {
                    return Result(TowerAwardStatus.RetryableFailure);
                }

                var wizard = session.Load<JObject>(wizardDocumentId);
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
                if (_capacity - occupiedIds < items.Count) { return Result(TowerAwardStatus.InventoryFull); }

                var magicSchoolBehavior = wizard["MagicSchoolBehavior"] as JObject;
                var currentXp = magicSchoolBehavior?["ExperiencePoints"]?.Value<int>();
                if (magicSchoolBehavior is null || currentXp is null) { return Result(TowerAwardStatus.NotEligible); }
                var liveWizard = wizard.ToObject<Wizard>();
                if (liveWizard?.MagicSchoolBehavior is null || liveWizard.GameStats is null) {
                    return Result(TowerAwardStatus.NotEligible);
                }

                var progressionId = ProgressionId(request.WizardId);
                var progression = session.Load<TowerPlayerProgression>(progressionId);
                var createProgression = progression is null;
                progression ??= new TowerPlayerProgression { WizardId = request.WizardId };
                progression.ClearedFloors ??= [];
                var firstClear = !progression.ClearedFloors.Contains(request.Floor);
                var experienceAfter = currentXp.Value;
                var levelsAwarded = 0;
                var maximumLevel = _getMaximumLevel();
                if (firstClear && liveWizard.MagicSchoolBehavior.Level < maximumLevel) {
                    var targetLevel = checked((byte)(liveWizard.MagicSchoolBehavior.Level + 1));
                    var levelThreshold = _getExperienceAtLevel(targetLevel);
                    experienceAfter = Math.Max(experienceAfter, levelThreshold);
                    _setLevel(liveWizard, targetLevel);
                    liveWizard.MagicSchoolBehavior.ExperiencePoints = experienceAfter;
                    levelsAwarded = 1;
                }
                var levelAfter = liveWizard.MagicSchoolBehavior.Level;
                var experienceAwarded = experienceAfter - currentXp.Value;

                magicSchoolBehavior["ExperiencePoints"] = liveWizard.MagicSchoolBehavior.ExperiencePoints;
                magicSchoolBehavior["Level"] = liveWizard.MagicSchoolBehavior.Level;
                if (wizard["GameStats"] is JObject gameStats) {
                    gameStats["Level"] = liveWizard.GameStats.Level;
                    gameStats["m_baseHitpoints"] = liveWizard.GameStats.m_baseHitpoints;
                    gameStats["m_baseMana"] = liveWizard.GameStats.m_baseMana;
                    gameStats["m_powerPipBase"] = liveWizard.GameStats.m_powerPipBase;
                }

                foreach (var item in items) {
                    var itemId = (ulong)item.m_globalID;
                    inventoryIds.Add(new JValue(itemId));
                    var itemDocumentId = ItemId(request.WizardId, itemId);
                    session.Store(item, itemDocumentId);
                    session.Advanced.GetMetadataFor(item)[Raven.Client.Constants.Documents.Metadata.Collection] = "WizardItems";
                }

                if (firstClear) { progression.ClearedFloors.Add(request.Floor); }
                var ticketsBefore = Math.Max(0, progression.Tickets);
                progression.Tickets = (int)Math.Min(int.MaxValue, (long)ticketsBefore + request.Tickets);
                var ticketsAwarded = progression.Tickets - ticketsBefore;
                progression.HighestFloorCleared = Math.Max(progression.HighestFloorCleared, request.HighestFloorCleared);
                progression.UpdatedUtc = DateTime.UtcNow;
                if (wizard["GameStats"] is JObject ticketStats) {
                    ticketStats["m_currentEventCurrency1"] = progression.Tickets;
                }
                if (createProgression) {
                    session.Store(progression, progressionId);
                    session.Advanced.GetMetadataFor(progression)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerProgressions";
                }

                var receipt = new TowerAwardReceipt {
                    AwardId = request.AwardId,
                    WizardId = request.WizardId,
                    ExperiencePointsAwarded = experienceAwarded,
                    LevelsAwarded = levelsAwarded,
                    TicketsAwarded = ticketsAwarded,
                    TicketsAfter = progression.Tickets,
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
                    ExperiencePointsAwarded = experienceAwarded,
                    LevelsAwarded = levelsAwarded,
                    TicketsAwarded = ticketsAwarded,
                    TicketsAfter = progression.Tickets,
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

    private static string RunDocumentId(string runId) => "tower-runs/" + Uri.EscapeDataString(runId);

    private IDocumentSession OpenWriteSession() {
        if (_ownership is null) {
            var normalSession = _store.OpenSession();
            normalSession.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            return normalSession;
        }

        return _store.OpenSession(new SessionOptions {
            TransactionMode = TransactionMode.ClusterWide,
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.None,
        });
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

    private static TowerAwardResult Result(TowerAwardStatus status) => new() { Status = status };
}
