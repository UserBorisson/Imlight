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

internal enum TowerTomeMenu { Floors, Spells, Gear, ResetConfirmation }

internal sealed class TowerTomeMenuState {
    private readonly ConcurrentDictionary<string, ulong> _playerIdsByActor = new();
    private readonly ConcurrentDictionary<ulong, TowerTomeMenu> _menus = new();

    internal void TrackPlayer(string actorKey, ulong playerId) => _playerIdsByActor[actorKey] = playerId;

    internal void SetMenu(ulong playerId, TowerTomeMenu menu) => _menus[playerId] = menu;

    internal TowerTomeMenu GetMenu(ulong playerId)
        => _menus.GetValueOrDefault(playerId, TowerTomeMenu.Floors);

    internal ulong? PlayerLeft(string actorKey) {
        if (!_playerIdsByActor.TryRemove(actorKey, out var playerId)) {
            return null;
        }

        _menus.TryRemove(playerId, out _);
        return playerId;
    }

    internal void ClearPlayer(ulong playerId) => _menus.TryRemove(playerId, out _);
}

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
    private const string ResetDisplayKey = "GUI_00010632";
    private const string UnknownDisplayKey = "GUI_Unknown";
    private const string UnknownItemDisplayKey = "GUI_UnknownItem";
    private static readonly ObjectSerializer s_serializer = new(Versionable: false, Behaviors: SerializerFlags.None);
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
    private readonly TowerTomeMenuState _menuState = new();
    private readonly ConcurrentDictionary<ulong, TomeMenuAction[]> _visibleActions = new();
    private readonly ConcurrentDictionary<ulong, string[]> _visibleServiceNames = new();
    private readonly ConcurrentDictionary<ulong, string> _notices = new();
    private readonly ConcurrentDictionary<ulong, PendingPurchase> _pendingPurchases = new();
    private readonly ConcurrentDictionary<ulong, string> _nativeTrainingSchools = new();
    private readonly ConcurrentDictionary<ulong, long> _nativeTrainingEpochs = new();
    private readonly ConcurrentDictionary<ulong, byte> _pendingEntrySpells = new();
    private readonly ConcurrentDictionary<ulong, (long Epoch, long Revision, DateTime ExpiresUtc)> _pendingResets = new();
    private TowerRunCollection _runs;
    private TowerEconomyCollection _economy;

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
        ResetFloors,
        ConfirmReset,
        CancelReset,
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
        string Name = "",
        TowerItemUpgradePreview UpgradePreview = null);

    private sealed record SpellChoice(uint TemplateId, string Name, int RequiredLevel,
        uint RequiredSpellId, bool IsLearned, bool IsPrimary);
    private sealed record GearChoice(WizClientObjectItem Item, WizItemTemplate Template,
        int Tier, float DamageBonusPercent, TowerItemUpgradePreview Preview);
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

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is not null && Entity.Zone?.TowerRunState is { Status: TowerRunStatus.Active }) {
            _pendingEntrySpells[playerWizard.CharId] = 1;
            var balance = GetEconomy().GetTicketBalance(playerWizard.CharId);
            playerWizard.GameStats.m_currentPvPCurrency = balance;
            playerActor.Tell(TowerArenaWallet.CreateClientUpdate(balance));
        }
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is not null && _pendingEntrySpells.TryRemove(playerWizard.CharId, out _)) {
            TowerSpellLearning.Reconcile(playerWizard, GetEconomy(), packet => playerActor.Tell(packet));
        }
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        var run = Entity.Zone?.TowerRunState;
        var sanctuary = TowerZonePaths.IsSanctuaryPath(Entity.Zone?.ZonePath);
        if (wizard is null || run is not { Status: TowerRunStatus.Active } || !sanctuary) {
            return [];
        }

        var menu = _menuState.GetMenu(wizard.CharId);
        if (menu == TowerTomeMenu.Spells) {
            return TowerNativeTraining.CreateOptions(wizard.MagicSchoolBehavior.MagicSchool.ToString(),
                _nativeTrainingSchools.GetValueOrDefault(wizard.CharId,wizard.MagicSchoolBehavior.MagicSchool.ToString()),
                wizard.MagicSchoolBehavior.Level,wizard.SpellbookBehavior.LearnedSpellTemplateIds);
        }

        var options = new List<ServiceOptionBase>();
        var actions = new List<TomeMenuAction>();
        switch (menu) {
            case TowerTomeMenu.ResetConfirmation:
                AddOption(options, actions, new TomeMenuAction(TomeActionKind.ConfirmReset), "GUI_Yes");
                AddOption(options, actions, new TomeMenuAction(TomeActionKind.CancelReset), "GUI_No");
                break;
            case TowerTomeMenu.Spells:
                BuildSpellOptions(wizard, options, actions);
                break;
            case TowerTomeMenu.Gear:
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
        if (menu == TowerTomeMenu.Floors) {
            // Native NPCs advertise their complete training inventory before the
            // client selects the training service. Custom submenu replacement is too late.
            var school=wizard.MagicSchoolBehavior.MagicSchool.ToString();
            var initialSchool=TowerNativeTraining.SelectInitialSchool(school,wizard.MagicSchoolBehavior.Level,
                wizard.SpellbookBehavior.LearnedSpellTemplateIds);
            return TowerNativeTraining.ComposeHubOptions(options,TowerNativeTraining.CreateOptions(school,initialSchool,
                wizard.MagicSchoolBehavior.Level,wizard.SpellbookBehavior.LearnedSpellTemplateIds));
        }
        return options;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter,
        CoreObject playerObject, string serviceName, uint serviceOptionIndex) {
        if (serviceName == "WizTrainingService") {
            BeginNativeTraining(playerActor,playerCharacter,playerObject);
            return; // Native Train subsequently sends MSG_TRAIN.
        }
        if (playerCharacter is null
            || !_visibleServiceNames.TryGetValue(playerCharacter.CharId, out var serviceNames)) {
            return;
        }

        // A native interaction also proves attach is complete if the wizard has not moved yet.
        if (_pendingEntrySpells.TryRemove(playerCharacter.CharId, out _)) {
            TowerSpellLearning.Reconcile(playerCharacter, GetEconomy(), packet => playerActor.Tell(packet));
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

        _menuState.TrackPlayer(GetPlayerActorKey(playerActor), playerCharacter.CharId);

        var action = actions[serviceOptionIndex];
        if (action.Kind is not (TomeActionKind.ResetFloors or TomeActionKind.ConfirmReset)) { _pendingResets.TryRemove(playerCharacter.CharId, out _); }
        if (action.Kind is not (TomeActionKind.PurchaseSpell or TomeActionKind.UpgradeItem)) {
            _pendingPurchases.TryRemove(playerCharacter.CharId, out _);
        }
        switch (action.Kind) {
            case TomeActionKind.ResetFloors:
                BeginResetConfirmation(playerCharacter);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.ConfirmReset:
                ResetFloors(playerCharacter);
                _menuState.SetMenu(playerCharacter.CharId, TowerTomeMenu.Floors);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.CancelReset:
                _menuState.SetMenu(playerCharacter.CharId, TowerTomeMenu.Floors);
                _notices[playerCharacter.CharId] = "Tower reset cancelled.";
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.PreviousPage:
            case TomeActionKind.NextPage:
                ChangePage(playerCharacter.CharId, action.Page, action.FloorPage);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.OpenSpells:
                BeginNativeTraining(playerActor,playerCharacter,playerObject);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.OpenGear:
                _menuState.SetMenu(playerCharacter.CharId, TowerTomeMenu.Gear);
                _gearPages[playerCharacter.CharId] = 0;
                _notices.TryRemove(playerCharacter.CharId, out _);
                SendOptions(playerActor, playerCharacter, reinteract: 2);
                return;
            case TomeActionKind.BackToFloors:
                _menuState.SetMenu(playerCharacter.CharId, TowerTomeMenu.Floors);
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
        var wizardLevel = Math.Max(1, playerCharacter.MagicSchoolBehavior?.Level ?? 1);
        if (action.Kind is TomeActionKind.SelectFloor or TomeActionKind.SelectHighest or TomeActionKind.SetMode) {
            candidate.TryDiscardUnstartedEncounter();
        }
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
        if (playerCharacter is null) { return; }
        _menuState.TrackPlayer(GetPlayerActorKey(playerActor), playerCharacter.CharId);
        if (_menuState.GetMenu(playerCharacter.CharId) == TowerTomeMenu.Floors) {
            StartNativeTrainingBridge(playerActor,playerCharacter);
        }
        if (reinteract == 0) {
            var notice = _notices.TryRemove(playerCharacter.CharId, out var value) ? value : string.Empty;
            SendMenuSummary(playerActor, playerCharacter, notice);
        }
    }

    public void OnInteractionRangeLeft(IActorRef playerActor, Wizard playerCharacter) {
        if (playerCharacter is not null) {
            TowerNativeUi.Unbind(playerCharacter.CharId,playerActor);
            playerActor.Tell(new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS {JobID=TowerNativeUi.TrainingJobId});
            playerActor.Tell(new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS {JobID=TowerNativeUi.TrainingRefreshJobId});
            ClearTransientPlayerState(playerCharacter.CharId);
        }
        _menuState.PlayerLeft(GetPlayerActorKey(playerActor));
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        TowerNativeUi.Unbind(id,playerActor);
        playerActor.Tell(new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS {JobID=TowerNativeUi.TrainingJobId});
        playerActor.Tell(new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS {JobID=TowerNativeUi.TrainingRefreshJobId});
        var playerId = _menuState.PlayerLeft(GetPlayerActorKey(playerActor));
        if (playerId.HasValue) {
            ClearTransientPlayerState(playerId.Value);
        }
    }

    private void ClearTransientPlayerState(ulong playerId) {
        _nativeTrainingSchools.TryRemove(playerId,out _);
        _nativeTrainingEpochs.TryRemove(playerId,out _);
        _pendingEntrySpells.TryRemove(playerId, out _);
        _menuState.ClearPlayer(playerId);
        _floorPages.TryRemove(playerId, out _);
        _spellPages.TryRemove(playerId, out _);
        _gearPages.TryRemove(playerId, out _);
        _visibleActions.TryRemove(playerId, out _);
        _visibleServiceNames.TryRemove(playerId, out _);
        _notices.TryRemove(playerId, out _);
        _pendingPurchases.TryRemove(playerId, out _);
        _pendingResets.TryRemove(playerId, out _);
    }

    private static string GetPlayerActorKey(IActorRef playerActor) => playerActor.Path.ToString();

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
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.OpenGear), EquipmentDisplayKey);
        AddOption(options, actions, new TomeMenuAction(TomeActionKind.ResetFloors), ResetDisplayKey);

        var firstFloor = checked(page * FloorsPerPage + 1);
        var pageLength = Math.Min((long)FloorsPerPage - 1, nextFloor - firstFloor);
        var lastFloor = firstFloor + pageLength;
        var floorCount = checked((int)pageLength + 1);
        for (var offset = 0; offset < floorCount; offset++) {
            var floor = firstFloor + offset;
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.SelectFloor, Floor: floor),
                GetBossDisplayKey(floor, run.RunSeed));
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
            var cost = 0;
            var levelEligible = (wizard.MagicSchoolBehavior?.Level ?? 1) >= choice.RequiredLevel;
            var prerequisiteSatisfied = choice.RequiredSpellId == 0
                || wizard.SpellbookBehavior?.HasSpell(choice.RequiredSpellId) == true;
            var canPurchase = !choice.IsPrimary && IsSpellPurchaseEligible(choice.IsLearned, prerequisiteSatisfied, levelEligible);
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
        var maxPage = Math.Max(0, (choices.Count - 1) / EntriesPerPage);
        var page = Math.Clamp(_gearPages.GetValueOrDefault(wizard.CharId), 0, maxPage);
        _gearPages[wizard.CharId] = page;
        foreach (var choice in choices.Skip(page * EntriesPerPage).Take(EntriesPerPage)) {
            var canPurchase = choice.Preview?.Eligible == true;
            var nextTier = choice.Preview?.DestinationTier ?? choice.Tier;
            var cost = choice.Preview?.Cost ?? 0;
            var itemName = GetItemName(choice.Template);
            AddOption(options, actions, new TomeMenuAction(TomeActionKind.UpgradeItem,
                TicketCost: cost,
                CanPurchase: canPurchase,
                ItemId: choice.Item.m_globalID,
                ItemTemplateId: (uint)choice.Template.m_templateID,
                ExpectedTier: choice.Tier,
                Name: itemName, UpgradePreview: choice.Preview), GetItemDisplayKey(choice.Template));
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
        return TowerSpellProgression.Spells
            .Where(spell => string.Equals(spell.School, schoolName, StringComparison.OrdinalIgnoreCase)
                || (spell.SecondaryTrainable && (string.IsNullOrEmpty(spell.RequiredPrimarySchool)
                    || string.Equals(spell.RequiredPrimarySchool, schoolName, StringComparison.OrdinalIgnoreCase))))
            .Select(spell => new SpellChoice(spell.TemplateId, spell.Name, spell.RequiredLevel,
                spell.RequiredSpellId, learned.Contains(spell.TemplateId),
                string.Equals(spell.School, schoolName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(choice => choice.RequiredLevel)
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<GearChoice> GetGearChoices(Wizard wizard) {
        var equippedItems = wizard.EquipmentBehavior?.EquippedItems ?? [];
        return equippedItems
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
                    upgrade?.SchemaVersion >= 2 ? upgrade.Tier : 0, upgrade?.DamageBonusPercent ?? 0,
                    GetEconomy().GetEquippedUpgradePreview(wizard.CharId, pair.Item.m_globalID));
            })
            .Where(choice => choice is not null)
            .OrderBy(choice => choice.Template.m_objectName?.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Item.m_globalID)
            .ToList();
    }

    private void ChangePage(ulong wizardId, int page, long floorPage) {
        switch (_menuState.GetMenu(wizardId)) {
            case TowerTomeMenu.Spells: _spellPages[wizardId] = Math.Max(0, page); break;
            case TowerTomeMenu.Gear: _gearPages[wizardId] = Math.Max(0, page); break;
            default: _floorPages[wizardId] = Math.Max(0, floorPage); break;
        }
    }

    private bool NativeTrainingContextAllowed(Wizard wizard) => wizard is not null
        && TowerZonePaths.IsSanctuaryPath(Entity.Zone?.ZonePath)
        && Entity.Zone?.TowerRunState is {Status:TowerRunStatus.Active} run && run.OwnerWizardId==wizard.CharId;

    private void StartNativeTrainingBridge(IActorRef playerActor,Wizard wizard) {
        if(!NativeTrainingContextAllowed(wizard)) return;
        TowerNativeUi.Bind(wizard.CharId,playerActor,Entity.ZoneRef,Entity.SelfRef);
        var initialSchool=TowerNativeTraining.SelectInitialSchool(wizard.MagicSchoolBehavior.MagicSchool.ToString(),
            wizard.MagicSchoolBehavior.Level,wizard.SpellbookBehavior.LearnedSpellTemplateIds);
        playerActor.Tell(new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS {JobID=TowerNativeUi.TrainingJobId});
        playerActor.Tell(new GAME_5_PROTOCOL.MSG_START_CLIENT_PROCESS {
            OwnerGID=wizard.CharId,JobID=TowerNativeUi.TrainingJobId,
            ScriptName=$"Tower/TrainingTabs_{initialSchool}.lua",
        });
    }

    private void BeginNativeTraining(IActorRef playerActor,Wizard wizard,CoreObject playerObject=null) {
        if(!NativeTrainingContextAllowed(wizard) || NativeTrainingIsActive(wizard)) return;
        _menuState.TrackPlayer(GetPlayerActorKey(playerActor),wizard.CharId);
        _nativeTrainingSchools[wizard.CharId]=TowerNativeTraining.SelectInitialSchool(wizard.MagicSchoolBehavior.MagicSchool.ToString(),
            wizard.MagicSchoolBehavior.Level,wizard.SpellbookBehavior.LearnedSpellTemplateIds);
        _nativeTrainingEpochs[wizard.CharId]=Entity.Zone.TowerRunState.AuthorityEpoch;
        TowerNativeUi.Bind(wizard.CharId,playerActor,Entity.ZoneRef,Entity.SelfRef);
        _menuState.SetMenu(wizard.CharId,TowerTomeMenu.Spells);
        _notices.TryRemove(wizard.CharId,out _);
        Logger.Debug("Tower native training opened for wizard {0}.",Logger.Args(wizard.CharId));
    }

    [MessageHandler(typeof(TowerNativeUiCommand))]
    private void ReceiveNativeUiCommand(TowerNativeUiCommand message) {
        var wizard=message.Wizard;
        if(message.Action=="opened" && Equals(message.ZoneActor,Entity.ZoneRef)) {
            BeginNativeTraining(message.PlayerActor,wizard);
            return;
        }
        if(!NativeTrainingIsActive(wizard) || !Equals(message.ZoneActor,Entity.ZoneRef)) return;
        if(message.Action=="refresh") {
            Logger.Debug("Tower native trainer refresh callback for wizard {0}, school {1}.",
                Logger.Args(wizard.CharId,_nativeTrainingSchools.GetValueOrDefault(wizard.CharId,"")));
            SendOptions(message.PlayerActor,wizard,reinteract:4);
            return;
        }
        if(message.Action=="close") {
            _menuState.SetMenu(wizard.CharId,TowerTomeMenu.Floors);
            _nativeTrainingEpochs.TryRemove(wizard.CharId,out _);
            TowerNativeUi.Unbind(wizard.CharId,message.PlayerActor);
            SendOptions(message.PlayerActor,wizard,reinteract:0);
            StartNativeTrainingBridge(message.PlayerActor,wizard);
            return;
        }
        if(message.Action=="school" && message.Value is "Fire" or "Ice" or "Storm" or "Myth" or "Life" or "Death" or "Balance" or "Astral") {
            _nativeTrainingSchools[wizard.CharId]=message.Value;
            SendOptions(message.PlayerActor,wizard,reinteract:2);
        }
    }
    private bool NativeTrainingIsActive(Wizard wizard) => wizard is not null
        && TowerZonePaths.IsSanctuaryPath(Entity.Zone?.ZonePath)
        && Entity.Zone?.TowerRunState is {Status:TowerRunStatus.Active} run && run.OwnerWizardId==wizard.CharId
        && _nativeTrainingEpochs.TryGetValue(wizard.CharId,out var epoch) && epoch==run.AuthorityEpoch
        && _menuState.GetMenu(wizard.CharId)==TowerTomeMenu.Spells;
    internal void RequestNativeTraining(IActorRef playerActor,Wizard wizard,int index)
        => Entity.SelfRef.Tell(new TowerNativeTrainRequest(wizard,playerActor,index));
    internal void RequestNativeReinteraction(IActorRef playerActor,Wizard wizard)
        => Entity.SelfRef.Tell(new TowerNativeUiCommand(wizard,playerActor,Entity.ZoneRef,"refresh",""));
    [MessageHandler(typeof(TowerNativeTrainRequest))]
    private void ReceiveNativeTrain(TowerNativeTrainRequest message) {
        var wizard=message.Wizard;
        var unlock=TowerNativeTraining.ResolveIndex(message.TrainingIndex);
        if(!NativeTrainingIsActive(wizard) || unlock is null
            || !TowerNativeTraining.MatchesSchool(unlock.School,_nativeTrainingSchools.GetValueOrDefault(wizard.CharId,""))) return;
        var action=new TomeMenuAction(TomeActionKind.PurchaseSpell,SpellTemplateId:unlock.TemplateId,
            SpellLevel:unlock.RequiredLevel,RequiredSpellId:unlock.RequiredSpellId,TicketCost:0,CanPurchase:true,Name:unlock.Name);
        var knownBefore=wizard.SpellbookBehavior.HasSpell(unlock.TemplateId);
        PurchaseSpell(message.PlayerActor,wizard,action,nativeTraining:true);
        if(knownBefore || !wizard.SpellbookBehavior.HasSpell(unlock.TemplateId))
            message.PlayerActor.Tell(new WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE {SpellID=unlock.TemplateId,Success=0});
        // Native trainer completion manages its own overlay and learned row refresh.
    }

    private void BeginResetConfirmation(Wizard wizard) {
        var run = Entity.Zone?.TowerRunState;
        if (!TowerZonePaths.IsSanctuaryPath(Entity.Zone?.ZonePath)
            || run is not {Status: TowerRunStatus.Active, EncounterLifecycle: TowerEncounterLifecycle.NotStarted}
            || run.OwnerWizardId != wizard.CharId) {
            _notices[wizard.CharId] = "Return to Floor 0 after combat to reset the Tower.";
            return;
        }
        _pendingResets[wizard.CharId] = (run.AuthorityEpoch, run.Revision, DateTime.UtcNow.AddMinutes(2));
        _menuState.SetMenu(wizard.CharId, TowerTomeMenu.ResetConfirmation);
    }

    private void ResetFloors(Wizard wizard) {
        var run = Entity.Zone?.TowerRunState;
        if (!TowerZonePaths.IsSanctuaryPath(Entity.Zone?.ZonePath)
            || run is not { Status: TowerRunStatus.Active, EncounterLifecycle: TowerEncounterLifecycle.NotStarted }
            || run.OwnerWizardId != wizard.CharId) {
            _pendingResets.TryRemove(wizard.CharId, out _);
            _notices[wizard.CharId] = "Return to Floor 0 after combat to reset the Tower.";
            return;
        }
        var confirmed = _pendingResets.TryRemove(wizard.CharId, out var pending)
            && pending.Epoch == run.AuthorityEpoch && pending.Revision == run.Revision
            && pending.ExpiresUtc >= DateTime.UtcNow;
        if (!confirmed) {
            _notices[wizard.CharId] = "Reset confirmation expired or Tower state changed. Select Reset to try again.";
            return;
        }
        _runs ??= new TowerRunCollection(PlayerDatabase.Instance.Store);
        var reset = _runs.TryResetFloors(run);
        if (reset is null) {
            _notices[wizard.CharId] = "Tower reset could not complete. Wait for pending rewards, then try again.";
            return;
        }
        Entity.Zone.CommitTowerRunState(reset);
        _floorPages[wizard.CharId] = 0;
        _notices[wizard.CharId] = "Tower floors reset. Floor 1 is available; Tickets and character progression were preserved.";
    }

    private void PurchaseSpell(IActorRef playerActor, Wizard wizard, TomeMenuAction action, bool nativeTraining = false) {
        if (action.SpellTemplateId == 0) {
            _notices[wizard.CharId] = "That spell is unavailable.";
            return;
        }
        var unlock = TowerSpellProgression.Find(action.SpellTemplateId);
        if (unlock?.School.Equals(wizard.MagicSchoolBehavior?.MagicSchool.ToString(), StringComparison.OrdinalIgnoreCase) == true) {
            _notices[wizard.CharId] = wizard.SpellbookBehavior?.HasSpell(action.SpellTemplateId) == true
                ? $"{unlock.Name}: learned primary-school spell."
                : $"{unlock.Name}: automatically learned at level {unlock.RequiredLevel}"
                    + (unlock.MilestoneFloor > 0 ? $" or after clearing floor {unlock.MilestoneFloor}." : ".");
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
            || !TowerSpellProgression.EligibleSecondary(TowerSpellProgression.Find(action.SpellTemplateId),
                wizard.MagicSchoolBehavior?.MagicSchool.ToString(), wizard.MagicSchoolBehavior?.Level ?? 1,
                wizard.SpellbookBehavior.LearnedSpellTemplateIds)) {
            _notices[wizard.CharId] = "That spell is unavailable for this school.";
            return;
        }

        var result = GetEconomy().PurchaseSpell(wizard.CharId, action.SpellTemplateId, action.TicketCost);
        if (result.Status is TowerPurchaseStatus.Purchased or TowerPurchaseStatus.AlreadyPurchased) {
            wizard.SpellbookBehavior.LearnedSpellTemplateIds ??= [];
            wizard.SpellbookBehavior.AddSpellToBook(spell);
            wizard.GameStats.m_currentPvPCurrency = GetEconomy().GetTicketBalance(wizard.CharId);
        playerActor.Tell(TowerArenaWallet.CreateClientUpdate(wizard.GameStats.m_currentPvPCurrency));
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int)action.SpellTemplateId });
            if(nativeTraining) playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE {
                SpellID=action.SpellTemplateId,DisplayText="WizTraining_00000040",Success=1,
            });
            else playerActor.Tell(TowerSpellLearning.CreateAcquisitionPopup([action.SpellTemplateId]));
            _notices[wizard.CharId] = $"Learned {action.Name}. Arena Tickets: {result.TicketsAfter}.";
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
            _notices[wizard.CharId] = "No stronger target is unlocked for that equipped item. Gain levels and clear Tower floors.";
            return;
        }

        var item = wizard.EquipmentBehavior?.GetItem(action.ItemId)
            ?? wizard.InventoryBehavior?.GetItem(action.ItemId);
        if (item is null || item.m_templateID != action.ItemTemplateId) {
            _notices[wizard.CharId] = "That item is no longer in your inventory.";
            return;
        }

        var result = GetEconomy().UpgradeEquippedItem(wizard.CharId, action.ItemId, action.ItemTemplateId,
            action.ExpectedTier, action.TicketCost);
        if (result.Status != TowerPurchaseStatus.Purchased) {
            _notices[wizard.CharId] = result.Status switch {
                TowerPurchaseStatus.InsufficientTickets => $"Not enough Tickets to upgrade {action.Name}.",
                TowerPurchaseStatus.MaximumTierReached => $"{action.Name} is already fully upgraded.",
                TowerPurchaseStatus.TierChanged => $"{action.Name} changed. Review its current tier and cost.",
                _ => $"Could not upgrade {action.Name} ({result.Status}).",
            };
            return;
        }

        wizard.GameStats.m_currentPvPCurrency = GetEconomy().GetTicketBalance(wizard.CharId);
        playerActor.Tell(TowerArenaWallet.CreateClientUpdate(wizard.GameStats.m_currentPvPCurrency));
        wizard.TowerItemUpgrades ??= new TowerItemUpgradeBook();
        wizard.TowerItemUpgrades.ItemUpgrades ??= [];
        var prior = wizard.TowerItemUpgrades.ItemUpgrades.FirstOrDefault(state => state.ItemId == action.ItemId);
        var upgraded = result.Upgrade;
        if (prior is not null) wizard.TowerItemUpgrades.ItemUpgrades.Remove(prior);
        wizard.TowerItemUpgrades.ItemUpgrades.Add(upgraded);

        if (wizard.EquipmentBehavior.HasItemEquipped(action.ItemId)) {
            var template = CoreObjectFactory.GetCoreTemplate(action.ItemTemplateId) as WizItemTemplate;
            if (template is not null) {
                var removed = CharacterEffectHelper.RemoveEffectsFromWizard(wizard, template, action.ItemId);
                var added = CharacterEffectHelper.AddEffectsToWizard(wizard, template, action.ItemId);
                SendEffectChanges(playerActor, wizard.CharId, removed, added);
            }
        }

        _notices[wizard.CharId] = $"Upgraded {action.Name} to tier {result.UpgradeTier}. Arena Tickets: {result.TicketsAfter}.";
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
            ? $"Learn {action.Name} for free? {expiry}"
            : $"Upgrade {action.Name}: tier {action.ExpectedTier} to {action.UpgradePreview?.DestinationTier}. "
                + $"Item stats {FormatStats(action.UpgradePreview?.Before)} ? {FormatStats(action.UpgradePreview?.After)}. "
                + AggregatePreview(wizard, action.UpgradePreview)
                + $"Cost {action.TicketCost} Arena Tickets. {expiry}";
        return false;
    }

    private static string AggregatePreview(Wizard wizard, TowerItemUpgradePreview preview) {
        if (preview is null || wizard.GameStats is null) return "";
        var stats = wizard.GameStats;
        var index = (int)MagicSchools.GetMagicSchool(preview.School).m_schoolIndex;
        float At(List<float> values) => values is not null && values.Count > index ? values[index] : 0;
        var before = new TowerItemStats(stats.m_baseHitpoints, stats.m_baseMana,
            stats.m_dmgBonusPercentAll + At(stats.m_dmgBonusPercent), stats.m_accBonusPercentAll + At(stats.m_accBonusPercent),
            stats.m_dmgReducePercentAll, stats.m_healBonusPercentAll);
        return $"Equipped totals ({preview.School}): {FormatStats(before)} to {FormatStats(TowerTomePresentation.AggregateAfter(before, preview.Before, preview.After))}. ";
    }

    private static string FormatStats(TowerItemStats stats) => stats is null ? "unavailable"
        : $"HP {stats.Health}, mana {stats.Mana}, damage {stats.Damage:P1}, accuracy {stats.Accuracy:P1}, "
            + $"resist {stats.Resistance:P1}, healing {stats.Healing:P1}";

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

    internal static bool IsSpellPurchaseEligible(bool isLearned,
        bool prerequisiteSatisfied, bool levelEligible)
        => !isLearned && prerequisiteSatisfied && levelEligible;

    private string GetBossDisplayKey(long floor, ulong runSeed) {
        var template = CoreObjectFactory.GetCoreTemplate(TowerContentCatalog.GetBossTemplateId(floor, runSeed)) as GameObjectTemplate;
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
        var confirmingReset = _menuState.GetMenu(wizard.CharId) == TowerTomeMenu.ResetConfirmation;
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

        if (confirmingReset) {
            memento = TowerTomeDialogs.CreateResetConfirmation(template?.m_displayName ?? "GUI_Unknown",
                template?.m_sIcon ?? string.Empty, options);
        }
        if (!s_serializer.Serialize(memento, 4, out var data)) {
            Logger.Error("Tower Tome could not serialize the {0} menu.", Logger.Args(_menuState.GetMenu(wizard.CharId)));
            return;
        }
        var nativeTraining = _menuState.GetMenu(wizard.CharId) == TowerTomeMenu.Spells;
        // Ask the native controller to request fresh options. Leaving service
        // range here destroys the context needed by that native round trip.
        if(nativeTraining && reinteract==2) {
            playerActor.Tell(TowerNativeTraining.CreateRefreshRequest());
            return;
        }
        if (reinteract == 2 && !nativeTraining) {
            playerActor.Tell(new GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE {
                MobileID = Entity.ActiveGameObject.m_globalID.Full,
            });
        }
        playerActor.Tell(new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS {
            MobileID = Entity.ActiveGameObject.m_globalID.Full,
            Options = data,
            Reinteract = reinteract,
        });
        if(nativeTraining) return;
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

    private static string GetActionServiceName(TowerTomeMenu menu, TomeMenuAction action) {
        var identity = action.Kind switch {
            TomeActionKind.SelectFloor => $"SelectFloor_{action.Floor.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.SetMode => $"SetMode_{action.Mode}",
            TomeActionKind.PreviousPage when menu == TowerTomeMenu.Floors
                => $"PreviousFloorPage_{action.FloorPage.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.NextPage when menu == TowerTomeMenu.Floors
                => $"NextFloorPage_{action.FloorPage.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.PreviousPage => $"PreviousPage_{action.Page.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.NextPage => $"NextPage_{action.Page.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.SelectHighest => $"SelectHighest_{action.Floor.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TomeActionKind.OpenSpells => "OpenSpells",
            TomeActionKind.OpenGear => "OpenGear",
            TomeActionKind.ResetFloors => "ResetFloors",
            TomeActionKind.ConfirmReset => "ConfirmReset",
            TomeActionKind.CancelReset => "CancelReset",
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
        var menuName = _menuState.GetMenu(wizard.CharId) switch {
            TowerTomeMenu.Spells => "spells",
            TowerTomeMenu.Gear => "equipment upgrades",
            _ => "floors",
        };
        var selectionHint = _menuState.GetMenu(wizard.CharId) switch {
            TowerTomeMenu.Spells => "Select a spell by its name.",
            TowerTomeMenu.Gear => "Select an item by its name.",
            _ => "Select boss names in floor order.",
        };
        SendServerMessage(playerActor,
            $"Tower {menuName}: floor {run.CurrentFloor}, "
            + TowerTomePresentation.ProgressSummary(wizard.MagicSchoolBehavior?.MagicSchool.ToString() ?? "", run.HighestFloorCleared,
                GetEconomy().GetPermanentClearHistory(wizard.CharId), wizard.SpellbookBehavior?.LearnedSpellTemplateIds ?? [])
            + " "
            + $"{run.ChallengeMode}, {tickets} Arena Tickets. {selectionHint}"
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
