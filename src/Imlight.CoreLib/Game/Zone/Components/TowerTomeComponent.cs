using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>Native NPC-style hub for Tower floors, school spells and owned-item upgrades.</summary>
internal sealed class TowerTomeComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const uint TomeTemplateId = 1233879;
    private const int FloorsPerPage = 20;
    private const int EntriesPerPage = 12;
    private const string TomeServiceName = "TowerTomeService";
    private const string ChallengeDisplayKey = "GUI_ShopSeedChallenge";
    private const string SpellsDisplayKey = "GUI_00000093";
    private const string EquipmentDisplayKey = "GUI_00000102";
    private const string PreviousDisplayKey = "GUI_00001429";
    private const string NextDisplayKey = "GUI_Next";
    private const string UnknownDisplayKey = "GUI_Unknown";
    private const string UnknownItemDisplayKey = "GUI_UnknownItem";
    private const string TowerItemUpgradeEffectName = "TowerItemUpgradeAllDamage";
    private static readonly ObjectSerializer s_serializer = new(Behaviors: SerializerFlags.None);
    private static readonly CoreObjectSerializer s_effectSerializer = new(
        versionable: false,
        behaviors: SerializerFlags.None);
    private static readonly HashSet<EquipmentSlotType> s_upgradeableSlots = [
        EquipmentSlotType.Hat,
        EquipmentSlotType.Robe,
        EquipmentSlotType.Shoes,
        EquipmentSlotType.Weapon,
        EquipmentSlotType.Athame,
        EquipmentSlotType.Amulet,
        EquipmentSlotType.Ring,
        EquipmentSlotType.Deck,
    ];

    private readonly ConcurrentDictionary<ulong, long> _floorPages = new();
    private readonly ConcurrentDictionary<ulong, int> _spellPages = new();
    private readonly ConcurrentDictionary<ulong, int> _gearPages = new();
    private readonly ConcurrentDictionary<ulong, TomeMenu> _menus = new();
    private readonly ConcurrentDictionary<ulong, TomeMenuAction[]> _visibleActions = new();
    private readonly ConcurrentDictionary<ulong, string[]> _visibleServiceNames = new();
    private readonly ConcurrentDictionary<ulong, string> _notices = new();
    private readonly ConcurrentDictionary<ulong, PendingPurchase> _pendingPurchases = new();
    private TowerRunCollection _runs;
    private TowerEconomyCollection _economy;

    private enum TomeMenu { Floors, Spells, Gear }
    private enum TomeActionKind {
        SelectFloor,
        SetMode,
        PreviousPage,
        NextPage,
        SelectHighest,
        OpenSpells,
        OpenGear,
        BackToFloors,
        PurchaseSpell,
        UpgradeItem,
    }

    private sealed record TomeMenuAction(
        TomeActionKind Kind,
        long Floor = 0,
        long FloorPage = 0,
        TowerChallengeMode Mode = TowerChallengeMode.Normal,
        int Page = 0,
        uint SpellTemplateId = 0,
        int SpellLevel = 0,
        uint RequiredSpellId = 0,
        int TicketCost = 0,
        bool CanPurchase = false,
        ulong ItemId = 0,
        uint ItemTemplateId = 0,
        int ExpectedTier = 0,
        int MaximumTier = 0,
        float DamageBonusPerTier = 0,
        float CurrentDamageBonusPercent = 0,
        string Name = "");

    private sealed record SpellChoice(uint TemplateId, string Name, int RequiredLevel,
        uint RequiredSpellId, bool IsLearned);
    private sealed record GearChoice(WizClientObjectItem Item, WizItemTemplate Template,
        int Tier, float DamageBonusPercent);
    private sealed record PendingPurchase(TomeActionKind Kind, uint SpellTemplateId,
        ulong ItemId, uint ItemTemplateId, int ExpectedTier, int TicketCost, DateTime ExpiresUtc);

    public string ServiceName => TomeServiceName;
    public string NpcIcon => "Registrar";
    public string NpcNameKey => null;
    public string NpcTextKey => "GUI_NPCInteractText";
    public WizBangs WizBang => WizBangs.Registrar;
    public string StateName => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey => null;
    public float DEFAULT_INTERACTION_RADIUS => 500.0f;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObject && gameObject.m_templateID == TomeTemplateId;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        var run = Entity.Zone?.TowerRunState;
        var sanctuary = TowerZonePaths.IsSanctuaryPath(Entity.Zone?.ZonePath);
        if (wizard is null || run is not { Status: TowerRunStatus.Active } || !sanctuary) {
            return [];
        }

        _menus.TryGetValue(wizard.CharId, out var menu);
        var options = new List<ServiceOptionBase>();
        var actions = new List<TomeMenuAction>();
        switch (menu) {
            case TomeMenu.Spells:
                BuildSpellOptions(wizard, options, actions);
                break;
            case TomeMenu.Gear:
                BuildGearOptions(wizard, options, actions);
                break;
            default:
                BuildFloorOptions(wizard, run, options, actions);
                break;
        }

        var visibleActions = actions.ToArray();
        var serviceNames = visibleActions.Select(action => GetActionServiceName(menu, action)).ToArray();
        for (var index = 0; index < options.Count; index++) {
            options[index].m_serviceName = serviceNames[index];
        }
        _visibleActions[wizard.CharId] = visibleActions;
        _visibleServiceNames[wizard.CharId] = serviceNames;
        return options;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter,
        CoreObject playerObject, string serviceName, uint serviceOptionIndex) {
        if (playerCharacter is null
            || !_visibleServiceNames.TryGetValue(playerCharacter.CharId, out var serviceNames)) {
            return;
        }

        var actionIndex = TowerTomeOptionRouter.ResolveActionIndex(serviceNames, serviceName, serviceOptionIndex);
        if (actionIndex < 0) { return; }

        OnServiceInteraction(playerActor, playerCharacter, playerObject, (uint) actionIndex);
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter,
        CoreObject playerObject, uint serviceOptionIndex) {
        if (playerCharacter is null || playerObject is null
            || !_visibleActions.TryGetValue(playerCharacter.CharId, out var actions)
            || serviceOptionIndex >= actions.Length) {
            return;
        }

        var action = actions[serviceOptionIndex];
        if (action.Kind is not (TomeActionKind.PurchaseSpell or TomeActionKind.UpgradeItem)) {
            _pendingPurchases.TryRemove(playerCharacter.CharId, out _);
        }
        switch (action.Kind) {
            case TomeActionKind.PreviousPage:
            case TomeActionKind.NextPage:
                ChangePage(playerCharacter.CharId, action.Page, action.FloorPage);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.OpenSpells:
                _menus[playerCharacter.CharId] = TomeMenu.Spells;
                _spellPages[playerCharacter.CharId] = 0;
                _notices.TryRemove(playerCharacter.CharId, out _);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.OpenGear:
                _menus[playerCharacter.CharId] = TomeMenu.Gear;
                _gearPages[playerCharacter.CharId] = 0;
                _notices.TryRemove(playerCharacter.CharId, out _);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.BackToFloors:
                _menus[playerCharacter.CharId] = TomeMenu.Floors;
                _notices.TryRemove(playerCharacter.CharId, out _);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.PurchaseSpell:
                if (!action.CanPurchase) {
                    _pendingPurchases.TryRemove(playerCharacter.CharId, out _);
                    PurchaseSpell(playerActor, playerCharacter, action);
                }
                else if (ConfirmPurchase(playerCharacter, action)) {
                    PurchaseSpell(playerActor, playerCharacter, action);
                }
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.UpgradeItem:
                if (!action.CanPurchase) {
                    _pendingPurchases.TryRemove(playerCharacter.CharId, out _);
                    UpgradeItem(playerActor, playerCharacter, action);
                }
                else if (ConfirmPurchase(playerCharacter, action)) {
                    UpgradeItem(playerActor, playerCharacter, action);
                }
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
        }

        if (Entity.Zone?.TowerRunState is not { Status: TowerRunStatus.Active } currentRun) { return; }
        var candidate = currentRun.Copy();
        var wizardLevel = playerCharacter.MagicSchoolBehavior?.Level ?? 1;
        var accepted = action.Kind switch {
            TomeActionKind.SelectFloor => candidate.TrySelectFloor(action.Floor, wizardLevel),
            TomeActionKind.SelectHighest => candidate.TrySelectFloor(action.Floor, wizardLevel),
            TomeActionKind.SetMode => candidate.TrySetChallengeMode(action.Mode, wizardLevel),
            _ => false,
        };

        if (accepted && SaveRun(candidate, currentRun.AuthorityEpoch)) {
            if (action.Kind is TomeActionKind.SelectFloor or TomeActionKind.SelectHighest) {
                _floorPages[playerCharacter.CharId] = (candidate.CurrentFloor - 1) / FloorsPerPage;
            }
            _notices.TryRemove(playerCharacter.CharId, out _);
            Logger.Information("Wizard {WizardId} selected Tower floor {Floor} in {Mode} mode.",
                Logger.Args(playerCharacter.CharId, candidate.CurrentFloor, candidate.ChallengeMode));
        }
        else {
            _notices[playerCharacter.CharId] = "That floor or challenge mode is not available for your current level.";
        }
        SendOptions(playerActor, playerCharacter, reinteract: 2);
    }

    public void OnServiceOptionsSent(IActorRef playerActor, Wizard playerCharacter, int reinteract) {
        if (reinteract == 0 && playerCharacter is not null) {
            var notice = _notices.TryRemove(playerCharacter.CharId, out var value) ? value : string.Empty;
            SendMenuSummary(playerActor, playerCharacter, notice);
        }
    }

    private void BuildFloorOptions(Wizard wizard, TowerRunState run,
        List<ServiceOptionBase> options, List<TomeMenuAction> actions) {
        var nextFloor = run.HighestFloorCleared == long.MaxValue ? long.MaxValue : run.HighestFloorCleared + 1;
        var maxPage = (nextFloor - 1) / FloorsPerPage;
        var page = Math.Clamp(_floorPages.GetValueOrDefault(wizard.CharId), 0, maxPage);
        _floorPages[wizard.CharId] = page;
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.SetMode,
            Mode: run.ChallengeMode == TowerChallengeMode.Normal
                ? TowerChallengeMode.ScaledChallenge : TowerChallengeMode.Normal),
            ChallengeDisplayKey);
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.SelectHighest, nextFloor),
            NextDisplayKey);
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.OpenSpells), SpellsDisplayKey);
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.OpenGear), EquipmentDisplayKey);

        var firstFloor = checked(page * FloorsPerPage + 1);
        var pageLength = Math.Min((long)FloorsPerPage - 1, nextFloor - firstFloor);
        var lastFloor = firstFloor + pageLength;
        var floorCount = checked((int)pageLength + 1);
        for (var offset = 0; offset < floorCount; offset++) {
            var floor = firstFloor + offset;
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.SelectFloor, Floor: floor),
                GetBossDisplayKey(floor));
        }

        if (page > 0) {
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.PreviousPage, FloorPage: page - 1),
                PreviousDisplayKey);
        }
        if (lastFloor < nextFloor) {
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.NextPage, FloorPage: page + 1),
                NextDisplayKey);
        }
    }

    private void BuildSpellOptions(Wizard wizard, List<ServiceOptionBase> options,
        List<TomeMenuAction> actions) {
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.BackToFloors), PreviousDisplayKey);
        var choices = GetSchoolSpellChoices(wizard);
        var maxPage = Math.Max(0, (choices.Count - 1) / EntriesPerPage);
        var page = Math.Clamp(_spellPages.GetValueOrDefault(wizard.CharId), 0, maxPage);
        _spellPages[wizard.CharId] = page;
        foreach (var choice in choices.Skip(page * EntriesPerPage).Take(EntriesPerPage)) {
            var cost = GetSpellTicketCost(choice.RequiredLevel);
            var levelEligible = (wizard.MagicSchoolBehavior?.Level ?? 1) >= choice.RequiredLevel;
            var prerequisiteSatisfied = choice.RequiredSpellId == 0
                || wizard.SpellbookBehavior?.HasSpell(choice.RequiredSpellId) == true;
            var canPurchase = IsSpellPurchaseEligible(choice.IsLearned, prerequisiteSatisfied, levelEligible);
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.PurchaseSpell,
                SpellTemplateId: choice.TemplateId,
                SpellLevel: choice.RequiredLevel,
                RequiredSpellId: choice.RequiredSpellId,
                TicketCost: cost,
                CanPurchase: canPurchase,
                Name: choice.Name), GetSpellDisplayKey(choice.TemplateId));
        }
        if (page > 0) {
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.PreviousPage, Page: page - 1), PreviousDisplayKey);
        }
        if (page < maxPage) {
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.NextPage, Page: page + 1), NextDisplayKey);
        }
    }

    private void BuildGearOptions(Wizard wizard, List<ServiceOptionBase> options,
        List<TomeMenuAction> actions) {
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.BackToFloors), PreviousDisplayKey);
        var choices = GetGearChoices(wizard);
        var maximumTier = GetMaximumItemUpgradeTier();
        var damageBonusPerTier = GetDamageBonusPerTier();
        var maxPage = Math.Max(0, (choices.Count - 1) / EntriesPerPage);
        var page = Math.Clamp(_gearPages.GetValueOrDefault(wizard.CharId), 0, maxPage);
        _gearPages[wizard.CharId] = page;
        foreach (var choice in choices.Skip(page * EntriesPerPage).Take(EntriesPerPage)) {
            var canPurchase = choice.Tier < maximumTier;
            var nextTier = choice.Tier + 1;
            var cost = canPurchase ? GetItemUpgradeTicketCost(nextTier) : 0;
            var itemName = GetItemName(choice.Template);
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.UpgradeItem,
                TicketCost: cost,
                CanPurchase: canPurchase,
                ItemId: choice.Item.m_globalID,
                ItemTemplateId: (uint)choice.Template.m_templateID,
                ExpectedTier: choice.Tier,
                MaximumTier: maximumTier,
                DamageBonusPerTier: damageBonusPerTier,
                CurrentDamageBonusPercent: choice.DamageBonusPercent,
                Name: itemName), GetItemDisplayKey(choice.Template));
        }
        if (page > 0) {
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.PreviousPage, Page: page - 1), PreviousDisplayKey);
        }
        if (page < maxPage) {
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.NextPage, Page: page + 1), NextDisplayKey);
        }
    }

    private static List<SpellChoice> GetSchoolSpellChoices(Wizard wizard) {
        var schoolName = wizard.MagicSchoolBehavior?.MagicSchool.ToString();
        if (string.IsNullOrWhiteSpace(schoolName)) { return []; }
        var learned = (wizard.SpellbookBehavior?.LearnedSpellTemplateIds ?? []).ToHashSet();
        return SpiralDB.NpcSpellInventories.Values
            .Where(inventory => inventory?.Spells is not null)
            .SelectMany(inventory => inventory.Spells)
            .Where(entry => entry.TemplateID > 0UL && entry.TemplateID <= uint.MaxValue)
            .GroupBy(entry => (uint)entry.TemplateID)
            .Select(group => group.OrderBy(entry => entry.Level).First())
            .Select(entry => (Entry: entry, Template: CoreObjectFactory.GetCoreTemplate(entry.TemplateID) as SpellTemplate))
            .Where(item => item.Template is not null
                && !item.Template.m_Treasure
                && string.Equals(item.Template.m_sMagicSchoolName, schoolName, StringComparison.OrdinalIgnoreCase))
            .Select(item => {
                var requiredSpellId = item.Entry.RequiredSpellID > 0UL
                    && item.Entry.RequiredSpellID <= uint.MaxValue
                    ? (uint)item.Entry.RequiredSpellID : 0U;
                return new SpellChoice(
                    (uint)item.Entry.TemplateID,
                    SpellName(item.Template),
                    Math.Max(1, item.Entry.Level),
                    requiredSpellId,
                    learned.Contains((uint)item.Entry.TemplateID));
            })
            .OrderBy(choice => choice.RequiredLevel)
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<GearChoice> GetGearChoices(Wizard wizard) {
        var inventoryItems = wizard.InventoryBehavior?.Items ?? [];
        var equippedItems = wizard.EquipmentBehavior?.EquippedItems ?? [];
        return inventoryItems.Concat(equippedItems)
            .Where(item => item is not null && item.m_globalID != 0)
            .GroupBy(item => item.m_globalID)
            .Select(group => group.First())
            .Select(item => (Item: item, Template: CoreObjectFactory.GetCoreTemplate(item.m_templateID) as WizItemTemplate))
            .Where(pair => pair.Template?.m_adjectiveList is not null)
            .Select(pair => {
                EquipmentSlot slot;
                try { slot = ItemHelper.GetItemSlot(pair.Template); }
                catch { return null; }
                if (slot is null || !s_upgradeableSlots.Contains(slot.SlotType)) { return null; }
                var upgrade = wizard.TowerItemUpgrades?.ItemUpgrades?.FirstOrDefault(state => state.ItemId == pair.Item.m_globalID);
                return new GearChoice(pair.Item, pair.Template,
                    upgrade?.Tier ?? 0, upgrade?.DamageBonusPercent ?? 0);
            })
            .Where(choice => choice is not null)
            .OrderBy(choice => choice.Template.m_objectName?.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Item.m_globalID)
            .ToList();
    }

    private void ChangePage(ulong wizardId, int page, long floorPage) {
        switch (_menus.GetValueOrDefault(wizardId)) {
            case TomeMenu.Spells: _spellPages[wizardId] = Math.Max(0, page); break;
            case TomeMenu.Gear: _gearPages[wizardId] = Math.Max(0, page); break;
            default: _floorPages[wizardId] = Math.Max(0, floorPage); break;
        }
    }

    private void PurchaseSpell(IActorRef playerActor, Wizard wizard, TomeMenuAction action) {
        if (action.SpellTemplateId == 0) {
            _notices[wizard.CharId] = "That spell is unavailable.";
            return;
        }
        if (wizard.SpellbookBehavior?.HasSpell(action.SpellTemplateId) == true) {
            _notices[wizard.CharId] = $"{action.Name} is already learned.";
            return;
        }
        if ((wizard.MagicSchoolBehavior?.Level ?? 1) < action.SpellLevel) {
            _notices[wizard.CharId] = $"{action.Name} requires level {action.SpellLevel}.";
            return;
        }
        if (action.RequiredSpellId != 0
            && wizard.SpellbookBehavior?.HasSpell(action.RequiredSpellId) != true) {
            var requiredSpell = CoreObjectFactory.GetCoreTemplate(action.RequiredSpellId) as SpellTemplate;
            _notices[wizard.CharId] = requiredSpell is null
                ? $"Learn the required spell before {action.Name}."
                : $"Learn {SpellName(requiredSpell)} before {action.Name}.";
            return;
        }
        if (!action.CanPurchase) {
            _notices[wizard.CharId] = "That spell is not currently available.";
            return;
        }

        var template = CoreObjectFactory.GetCoreTemplate(action.SpellTemplateId) as SpellTemplate;
        var spell = template is null ? null : SpellFactory.GetSpell(action.SpellTemplateId);
        if (template is null || spell is null
            || !string.Equals(template.m_sMagicSchoolName, wizard.MagicSchoolBehavior?.MagicSchool.ToString(), StringComparison.OrdinalIgnoreCase)) {
            _notices[wizard.CharId] = "That spell is unavailable for this school.";
            return;
        }

        var result = GetEconomy().PurchaseSpell(wizard.CharId, action.SpellTemplateId, action.TicketCost);
        if (result.Status == TowerPurchaseStatus.Purchased) {
            wizard.SpellbookBehavior.LearnedSpellTemplateIds ??= [];
            wizard.SpellbookBehavior.AddSpellToBook(spell);
            wizard.GameStats.m_currentEventCurrency1 = result.TicketsAfter;
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int)action.SpellTemplateId });
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE {
                SpellID = action.SpellTemplateId,
                DisplayText = "WizTraining_00000040",
                Success = 1,
            });
            _notices[wizard.CharId] = $"Learned {action.Name}. Tower Tickets: {result.TicketsAfter}.";
        }
        else {
            _notices[wizard.CharId] = result.Status switch {
                TowerPurchaseStatus.AlreadyOwned or TowerPurchaseStatus.AlreadyPurchased => $"{action.Name} is already learned.",
                TowerPurchaseStatus.InsufficientTickets => $"Not enough Tickets to learn {action.Name}.",
                _ => $"Could not learn {action.Name} ({result.Status}).",
            };
        }
    }

    private void UpgradeItem(IActorRef playerActor, Wizard wizard, TomeMenuAction action) {
        if (!action.CanPurchase || action.ItemId == 0 || action.ItemTemplateId == 0) {
            _notices[wizard.CharId] = "That item is already at its maximum Tower tier.";
            return;
        }

        var item = wizard.EquipmentBehavior?.GetItem(action.ItemId)
            ?? wizard.InventoryBehavior?.GetItem(action.ItemId);
        if (item is null || item.m_templateID != action.ItemTemplateId) {
            _notices[wizard.CharId] = "That item is no longer in your inventory.";
            return;
        }

        var result = GetEconomy().UpgradeItem(wizard.CharId, action.ItemId, action.ItemTemplateId,
            action.ExpectedTier, action.TicketCost, action.MaximumTier, action.DamageBonusPerTier);
        if (result.Status != TowerPurchaseStatus.Purchased) {
            _notices[wizard.CharId] = result.Status switch {
                TowerPurchaseStatus.InsufficientTickets => $"Not enough Tickets to upgrade {action.Name}.",
                TowerPurchaseStatus.MaximumTierReached => $"{action.Name} is already fully upgraded.",
                TowerPurchaseStatus.TierChanged => $"{action.Name} changed. Review its current tier and cost.",
                _ => $"Could not upgrade {action.Name} ({result.Status}).",
            };
            return;
        }

        wizard.GameStats.m_currentEventCurrency1 = result.TicketsAfter;
        wizard.TowerItemUpgrades ??= new TowerItemUpgradeBook();
        wizard.TowerItemUpgrades.ItemUpgrades ??= [];
        var prior = wizard.TowerItemUpgrades.ItemUpgrades.FirstOrDefault(state => state.ItemId == action.ItemId);
        var upgraded = new TowerItemUpgradeState {
            ItemId = action.ItemId,
            TemplateId = action.ItemTemplateId,
            Tier = result.UpgradeTier,
            DamageBonusPercent = result.DamageBonusPercent,
            UpdatedUtc = DateTime.UtcNow,
        };
        if (prior is null) { wizard.TowerItemUpgrades.ItemUpgrades.Add(upgraded); }
        else {
            prior.Tier = upgraded.Tier;
            prior.DamageBonusPercent = upgraded.DamageBonusPercent;
            prior.UpdatedUtc = upgraded.UpdatedUtc;
        }

        if (wizard.EquipmentBehavior.HasItemEquipped(action.ItemId)) {
            var template = CoreObjectFactory.GetCoreTemplate(action.ItemTemplateId) as WizItemTemplate;
            if (template is not null) {
                var removed = CharacterEffectHelper.RemoveEffectsFromWizard(wizard, template, action.ItemId);
                var added = CharacterEffectHelper.AddEffectsToWizard(wizard, template, action.ItemId);
                SendEffectChanges(playerActor, wizard.CharId, removed, added);
            }
        }

        _notices[wizard.CharId] = $"Upgraded {action.Name} to tier {result.UpgradeTier}. Tower Tickets: {result.TicketsAfter}.";
    }

    private bool ConfirmPurchase(Wizard wizard, TomeMenuAction action) {
        var tickets = GetEconomy().GetTicketBalance(wizard.CharId);
        if (tickets < action.TicketCost) {
            _pendingPurchases.TryRemove(wizard.CharId, out _);
            var purchaseName = action.Kind == TomeActionKind.PurchaseSpell ? "learn" : "upgrade";
            _notices[wizard.CharId] = $"Not enough Tickets to {purchaseName} {action.Name}. "
                + $"Cost: {action.TicketCost}; balance: {tickets}.";
            return false;
        }

        var now = DateTime.UtcNow;
        var matches = _pendingPurchases.TryRemove(wizard.CharId, out var pending)
            && pending.ExpiresUtc >= now
            && pending.Kind == action.Kind
            && pending.SpellTemplateId == action.SpellTemplateId
            && pending.ItemId == action.ItemId
            && pending.ItemTemplateId == action.ItemTemplateId
            && pending.ExpectedTier == action.ExpectedTier
            && pending.TicketCost == action.TicketCost;
        if (matches) { return true; }

        _pendingPurchases[wizard.CharId] = new PendingPurchase(
            action.Kind,
            action.SpellTemplateId,
            action.ItemId,
            action.ItemTemplateId,
            action.ExpectedTier,
            action.TicketCost,
            now.AddSeconds(30));
        var expiry = "Select the same name again within 30 seconds to confirm.";
        _notices[wizard.CharId] = action.Kind == TomeActionKind.PurchaseSpell
            ? $"Learn {action.Name} for {action.TicketCost} Tickets? {expiry}"
            : $"Upgrade {action.Name}: tier {action.ExpectedTier} to {action.ExpectedTier + 1}, "
                + $"damage +{action.CurrentDamageBonusPercent:P0} to "
                + $"+{action.CurrentDamageBonusPercent + action.DamageBonusPerTier:P0}, "
                + $"cost {action.TicketCost} Tickets. {expiry}";
        return false;
    }

    private void SendEffectChanges(IActorRef playerActor, ulong wizardId,
        IEnumerable<GameEffectBase> removed, IEnumerable<GameEffectBase> added) {
        foreach (var effect in removed) {
            playerActor.Tell(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizardId,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }
        foreach (var effect in added) {
            var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
            if (!s_effectSerializer.Serialize(effect, flags, out var serialized)) {
                Logger.Error("Could not serialize Tower equipment upgrade effect {EffectId}.", Logger.Args(effect.m_effectNameID));
                continue;
            }
            playerActor.Tell(new GAME_5_PROTOCOL.MSG_ADDEFFECT {
                GameObjectID = wizardId,
                EffectData = serialized,
            });
        }
    }

    private static int GetSpellTicketCost(int requiredLevel) {
        var baseCost = Math.Max(1, ConfigurationManager.GetValue("Tower.SpellTicketBaseCost", 1));
        var levelCost = Math.Max(0, requiredLevel - 1) / 10;
        return (int)Math.Min(int.MaxValue, (long)baseCost + levelCost);
    }

    internal static bool IsSpellPurchaseEligible(bool isLearned,
        bool prerequisiteSatisfied, bool levelEligible)
        => !isLearned && prerequisiteSatisfied && levelEligible;

    private static int GetItemUpgradeTicketCost(int nextTier) {
        var baseCost = Math.Max(1, ConfigurationManager.GetValue("Tower.ItemUpgradeBaseCost", 5));
        return (int)Math.Min(int.MaxValue, (long)baseCost * Math.Max(1, nextTier));
    }

    private static int GetMaximumItemUpgradeTier()
        => Math.Clamp(ConfigurationManager.GetValue("Tower.ItemUpgradeMaxTier", 3), 1, 10);

    private static float GetDamageBonusPerTier() {
        var basisPoints = Math.Clamp(ConfigurationManager.GetValue("Tower.ItemUpgradeDamageBasisPoints", 100), 1, 500);
        return basisPoints / 10_000f;
    }

    private string GetBossPreviewName(long floor) {
        var template = CoreObjectFactory.GetCoreTemplate(TowerContentCatalog.GetBossTemplateId(floor)) as GameObjectTemplate;
        return template?.m_objectName?.ToString() ?? TowerContentCatalog.GetBossWorld(floor) + " boss";
    }

    private string GetBossDisplayKey(long floor) {
        var template = CoreObjectFactory.GetCoreTemplate(TowerContentCatalog.GetBossTemplateId(floor)) as GameObjectTemplate;
        return ResolveLocalizedDisplayKey(template?.m_displayName?.ToString(), UnknownDisplayKey);
    }

    private static string GetSpellDisplayKey(uint templateId) {
        var template = CoreObjectFactory.GetCoreTemplate(templateId) as SpellTemplate;
        return ResolveLocalizedDisplayKey(template?.m_displayName?.ToString(), UnknownDisplayKey);
    }

    private static string GetItemDisplayKey(WizItemTemplate template)
        => ResolveLocalizedDisplayKey(template?.m_displayName?.ToString(), UnknownItemDisplayKey);

    internal static string ResolveLocalizedDisplayKey(string candidate, string fallbackKey) {
        if (string.IsNullOrWhiteSpace(candidate)
            || !candidate.Contains('_')
            || candidate.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '-')) {
            return fallbackKey;
        }

        return candidate;
    }

    private static string SpellName(SpellTemplate template)
        => template?.m_name?.ToString() ?? "Unknown spell";

    private static string GetItemName(WizItemTemplate template)
        => template?.m_objectName?.ToString() ?? "Equipment";

    private bool SaveRun(TowerRunState candidate, long expectedEpoch) {
        _runs ??= new TowerRunCollection(PlayerDatabase.Instance.Store);
        if (!_runs.TrySave(candidate, expectedEpoch)) { return false; }
        Entity.Zone.CommitTowerRunState(candidate);
        return true;
    }

    private TowerEconomyCollection GetEconomy()
        => _economy ??= new TowerEconomyCollection(PlayerDatabase.Instance.Store);

    private void SendOptions(IActorRef playerActor, Wizard wizard, int reinteract) {
        var options = GetServiceOptions(wizard).ToList();
        if (options.Count == 0) { return; }

        var template = Entity.Template as GameObjectTemplate;
        var memento = new ServiceMementoBase {
            m_npcIcon = template?.m_sIcon ?? string.Empty,
            m_npcNameKey = "NPCFormats_Name",
            m_npcTextKey = NpcTextKey,
            m_serviceOptions = options,
            m_personaMadlibs = new MadlibBlock {
                m_blockToken = "NPC",
                m_madlibs = [new MadlibArgT_ByteString {
                    m_madlibArgument = template?.m_displayName ?? string.Empty,
                    m_madlibToken = "NAME",
                }],
            },
        };

        if (!s_serializer.Serialize(memento, 4, out var data)) { return; }
        playerActor.Tell(new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS {
            MobileID = Entity.ActiveGameObject.m_globalID.Full,
            Options = data,
            Reinteract = reinteract,
        });
        var notice = _notices.TryRemove(wizard.CharId, out var value) ? value : null;
        SendMenuSummary(playerActor, wizard, notice);
    }

    private static void AddOption(List<ServiceOptionBase> options, List<TomeMenuAction> actions,
        TomeMenuAction action, string display) {
        options.Add(new ServiceOptionBase {
            m_serviceName = TomeServiceName,
            m_displayKey = ResolveLocalizedDisplayKey(display, UnknownDisplayKey),
            m_iconKey = "Registrar",
        });
        actions.Add(action);
    }

    private static string GetActionServiceName(TomeMenu menu, TomeMenuAction action) {
        var identity = action.Kind switch {
            TomeActionKind.SelectFloor => $"SelectFloor_{action.Floor.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.SetMode => $"SetMode_{action.Mode}",
            TomeActionKind.PreviousPage when menu == TomeMenu.Floors
                => $"PreviousFloorPage_{action.FloorPage.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.NextPage when menu == TomeMenu.Floors
                => $"NextFloorPage_{action.FloorPage.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.PreviousPage => $"PreviousPage_{action.Page.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.NextPage => $"NextPage_{action.Page.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.SelectHighest => $"SelectHighest_{action.Floor.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.OpenSpells => "OpenSpells",
            TomeActionKind.OpenGear => "OpenGear",
            TomeActionKind.BackToFloors => "BackToFloors",
            TomeActionKind.PurchaseSpell => $"PurchaseSpell_{action.SpellTemplateId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.UpgradeItem => $"UpgradeItem_{action.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{action.ExpectedTier.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Unknown Tome action"),
        };

        return $"{TomeServiceName}_{menu}_{identity}";
    }

    private void SendMenuSummary(IActorRef playerActor, Wizard wizard, string notice = "") {
        if (playerActor is null || wizard is null
            || Entity.Zone?.TowerRunState is not { Status: TowerRunStatus.Active } run
            || !_visibleActions.TryGetValue(wizard.CharId, out var actions)) {
            return;
        }

        var tickets = GetEconomy().GetTicketBalance(wizard.CharId);
        var menuName = _menus.GetValueOrDefault(wizard.CharId) switch {
            TomeMenu.Spells => "spells",
            TomeMenu.Gear => "equipment upgrades",
            _ => "floors",
        };
        var selectionHint = _menus.GetValueOrDefault(wizard.CharId) switch {
            TomeMenu.Spells => "Select a spell by its name.",
            TomeMenu.Gear => "Select an item by its name.",
            _ => "Select boss names in floor order.",
        };
        SendServerMessage(playerActor,
            $"Tower {menuName}: floor {run.CurrentFloor}, highest cleared {run.HighestFloorCleared}, "
            + $"{run.ChallengeMode}, {tickets} tickets. {selectionHint}"
            + (string.IsNullOrWhiteSpace(notice) ? string.Empty : $" {notice}"));
    }

    private static void SendServerMessage(IActorRef playerActor, string message)
        => playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = message });
}

internal static class TowerTomeOptionRouter {
    public static int ResolveActionIndex(IReadOnlyList<string> visibleServiceNames,
        string serviceName, uint serviceOptionIndex) {
        if (visibleServiceNames is null) { return -1; }

        if (!string.IsNullOrEmpty(serviceName)) {
            for (var index = 0; index < visibleServiceNames.Count; index++) {
                if (string.Equals(visibleServiceNames[index], serviceName, StringComparison.Ordinal)) {
                    return index;
                }
            }

            if (!string.Equals(serviceName, "TowerTomeService", StringComparison.Ordinal)) {
                return -1;
            }
        }

        return serviceOptionIndex < visibleServiceNames.Count ? (int) serviceOptionIndex : -1;
    }
}
