using System.Collections;
using System.Reflection;
using Imlight.CoreLib.Game.Gifts;
using Imlight.CoreLib.Game.Tower;

internal static class TowerDuelLifecycleTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        EnemyPlanningTransmitsNativeInputLock();
        SparseSchoolStatsResolveWithoutBlockingCombat();
        EnemyPlanningAllowsHandMaintenanceOnly();
        TowerOpponentUsesNativePlayerAvatar();
        DefeatedWizardRetainsDazedPresentation();
        AiHealingUsesFractionalHealth();
        FiniteDeckDiscardsDoNotConsumeUndrawnCopies();
        CreatureAiUsesRawFriendlyTargetSlots();
        TowerTurnsAlwaysStartWithThePlayerAndAlternateAfterResolution();
        VictoryStaysInArenaWhileDefeatReturnsToSanctuary();
        TowerAdmissionRequiresEveryManifestEnemyAndUniqueActors();
        TowerKillsRequireCombatDeathEvidence();
        TechnicalResolutionCannotBecomeACombatWinOrLoss();
        LateTowerInterruptionAfterResolutionIsIgnored();
        TowerResourceWritesRequireCurrentNonterminalEncounter();
        LiveTowerIntentionalFleeRetainsManaDrainPolicy();
        LiveTowerDisconnectRemainsTechnicalAbort();
        OrdinaryDisconnectKeepsNativeFleeBehavior();
        InterruptedEncounterConsumesOneRecoveryAndRestoresEntryResources();
        Console.WriteLine("PASS: tower duel admission, death evidence, outcome classification, and interruption policy");
    }

    private static void EnemyPlanningTransmitsNativeInputLock() {
        var method = Type("Imlight.CoreLib.Game.Tower.TowerCombatTurnPolicy")
            .GetMethod("CreatePlanningUI", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Tower planning must transmit native AltTurn and AltTurnTeam controls");
        var enemy = (Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI)
            method.Invoke(null, new object[] { (Imcodec.Types.GID)123UL, 1, 1 })!;
        Check(enemy.AltTurn == 1 && enemy.AltTurnTeam == 1, "enemy phase communicates native alternating-turn card/target lock");
        var player = (Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI)
            method.Invoke(null, new object[] { (Imcodec.Types.GID)123UL, 0, 2 })!;
        Check(player.AltTurn == 2 && player.AltTurnTeam == 0, "next player phase restores casting using native current-turn state");
    }

    private static void DefeatedWizardRetainsDazedPresentation() {
        var type = Type("Imlight.CoreLib.Game.Zone.Core.TowerWizardEntity");
        var retainedState = type.GetMethod("GetRetainedPresentationState", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("AI wizard must retain its defeated presentation for later viewers");
        var entity = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        Type("Imlight.CoreLib.Game.Zone.Core.ZoneEntity").GetField("<ActiveGameObject>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(entity,
                new Imcodec.ObjectProperty.TypeCache.WizClientObject { m_globalID = 987654UL });
        Equal<object>(null, retainedState.Invoke(entity, null), "living wizard has no defeated presentation");
        type.GetField("_isDazed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(entity, true);
        var message = (Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL.MSG_ENTERSTATE)retainedState.Invoke(entity, null)!;
        Equal(987654UL, message.GameObjectID, "dazed state belongs to the same wizard object");
        Equal(Imcodec.Cryptography.StringHash.Compute("Emoting"), message.State, "native wizard emote state is retained");
        var codec = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None);
        if (!codec.Deserialize<Imcodec.ObjectProperty.TypeCache.EmoteStateOverrideInfo>((byte[])message.Data, 1, out var emote))
            throw new InvalidOperationException("Retained native dazed state must deserialize");
        Equal("Dazed", emote.m_emoteName.ToString(), "same body retains the Dazed emote");
        Equal(true, emote.m_loop, "dazed emote continues until leaving the room");
    }

    private static void AiHealingUsesFractionalHealth() {
        var stats = (Imlight.CoreLib.Shared.Behaviors.ServerWizGameStats)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Imlight.CoreLib.Shared.Behaviors.ServerWizGameStats));
        stats.m_baseHitpoints = 100;
        stats.m_currentHitpoints = 75;
        var method = Type("Imlight.CoreLib.Game.Zone.Components.CombatCreatureAIComponent")
            .GetMethod("HealthFraction", BindingFlags.Static | BindingFlags.NonPublic)!;
        Equal(.75f, (float)method.Invoke(null, [stats])!, "AI does not consider every injured wizard near death");
        stats.m_baseHitpoints = 0;
        Equal(1f, (float)method.Invoke(null, [stats])!, "invalid zero max health does not divide by zero");
    }

    private static void TowerOpponentUsesNativePlayerAvatar() {
        var type = Type("Imlight.CoreLib.Game.Tower.TowerWizardAvatar");
        var decorate = type.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic)!;
        var avatar = new Imcodec.ObjectProperty.TypeCache.WizardCharacterBehavior();
        var name = new Imcodec.ObjectProperty.TypeCache.ClientWizPlayerNameBehavior();
        var school = new Imcodec.ObjectProperty.TypeCache.ClientMagicSchoolBehavior();
        var obj = new Imcodec.ObjectProperty.TypeCache.WizClientObject {
            m_inactiveBehaviors = [avatar, name, school]
        };
        decorate.Invoke(null, [obj, new TowerEnemySpawn { SchoolId = "Fire", Level = 1 }, 1L]);
        Equal(1UL, obj.m_templateID.Full, "AI opponent has the native player template");
        Equal(Imcodec.ObjectProperty.TypeCache.eRace.Human, avatar.m_eRace, "opponent uses player human body");
        Equal(1, school.m_level, "native player level is visible");
        Equal(false, name.m_useRank, "opponent uses its explicit school/level line instead of a player rank title");
        Equal(true, name.m_wsNameOverride.ToString().Contains("\n<image;FireSchoolIcon> Level 1"), "opponent nameplate has mob-style school and logical level on a second line");
        var firstName = name.m_wsNameOverride.ToString();
        decorate.Invoke(null, [obj, new TowerEnemySpawn { SchoolId = "Fire", Level = 1 }, 1L]);
        Equal(firstName, name.m_wsNameOverride.ToString(), "floor identity survives replay");
        var codec = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false);
        const uint flags = 31;
        if (!codec.Serialize(avatar, flags, out var buffer)
            || !codec.Deserialize<Imcodec.ObjectProperty.TypeCache.WizardCharacterBehavior>((byte[])buffer, flags, out var decoded))
            throw new InvalidOperationException("Native wizard avatar must round-trip through the client codec");
        Equal(avatar.m_eGender, decoded.m_eGender, "native avatar gender survives wire codec");
    }

    private static void SparseSchoolStatsResolveWithoutBlockingCombat() {
        var schools = (IDictionary)Type("Imlight.CoreLib.Game.Spells.MagicSchools")
            .GetField("s_magicSchools", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        schools[2] = new Imcodec.ObjectProperty.TypeCache.MagicSchoolTemplate { m_schoolIndex = 2, m_schoolName = "Fire" };
        schools[20] = new Imcodec.ObjectProperty.TypeCache.MagicSchoolTemplate { m_schoolIndex = 20, m_schoolName = "Extra" };
        try {
            var circleType = Type("Imlight.CoreLib.Game.Combat.CombatDuelSubCircle");
            var circle = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(circleType);
            var method = circleType.GetMethod("GetStatBySchool", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(float));
            Equal(.2f, (float)method.Invoke(circle, [new List<float> { 0f, 0f, .2f }, "Fire"])!, "sparse vectors retain present school bonuses");
            Equal(0f, (float)method.Invoke(circle, [new List<float>(), "Fire"])!, "missing school bonuses resolve as zero");
        }
        finally { schools.Remove(2); schools.Remove(20); }
    }

    private static void EnemyPlanningAllowsHandMaintenanceOnly() {
        var policy = Type("Imlight.CoreLib.Game.Tower.TowerCombatTurnPolicy");
        var method = policy.GetMethod("CanMaintainHand", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing enemy-planning hand policy");
        Equal(true, (bool)method.Invoke(null, [true, 0])!, "player can maintain hand during planning");
        Equal(false, (bool)method.Invoke(null, [false, 0])!, "hand cannot change during execution");
        Equal(false, (bool)method.Invoke(null, [true, 1])!, "enemy cannot use player hand controls");
    }

    private static void FiniteDeckDiscardsDoNotConsumeUndrawnCopies() {
        const uint spellId = 4000000000;
        var cacheField = typeof(Imlight.CoreLib.Shared.Resources.CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<ulong, Imcodec.ObjectProperty.TypeCache.CoreTemplate>)cacheField.GetValue(null)!;
        cache[spellId] = new Imcodec.ObjectProperty.TypeCache.SpellTemplate {
            m_name = "Finite deck regression", m_sMagicSchoolName = "Fire"
        };
        try {
            var dataType = Type("Imlight.CoreLib.Game.Combat.CombatDeckSpellData");
            var data = Activator.CreateInstance(dataType)!;
            dataType.GetProperty("TemplateId")!.SetValue(data, spellId);
            dataType.GetProperty("Quantity")!.SetValue(data, 4U);
            var dataList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(dataType))!;
            dataList.Add(data);
            var deckType = Type("Imlight.CoreLib.Game.Combat.CombatDeck");
            var deck = deckType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
                .Invoke([dataList, null, (byte)1]);
            var getHand = deckType.GetMethod("GetHand", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var discard = deckType.GetMethod("Discard", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var remaining = deckType.GetProperty("RemainingCardCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var handProperty = deckType.GetProperty("LastGivenHand", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var consumed = 0; consumed < 4; consumed++) {
                getHand.Invoke(deck, null);
                var hand = (IList)handProperty.GetValue(deck)!;
                Equal(1, hand.Count, "each of four original copies can be drawn exactly once");
                Equal(3 - consumed, (int)remaining.GetValue(deck)!, "drawing consumes exactly one copy");
                discard.Invoke(deck, [hand[0]]);
            }
            getHand.Invoke(deck, null);
            Equal(0, ((IList)handProperty.GetValue(deck)!).Count, "finite deck exhausts after four cards");
        }
        finally { cache.TryRemove(spellId, out _); }
    }

    private static void VictoryStaysInArenaWhileDefeatReturnsToSanctuary() {
        var policyType = Type("Imlight.CoreLib.Game.Tower.TowerDuelOutcomePolicy");
        var shouldReturn = policyType.GetMethod("ShouldReturnToSanctuary",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: [typeof(TowerResolutionResult)], modifiers: null)
            ?? throw new InvalidOperationException("Missing TowerDuelOutcomePolicy.ShouldReturnToSanctuary");
        Equal(false, (bool)shouldReturn.Invoke(null, [TowerResolutionResult.Victory])!,
            "a committed win leaves the player on the combat floor");
        Equal(true, (bool)shouldReturn.Invoke(null, [TowerResolutionResult.Defeat])!,
            "a defeat returns the player to Floor 0");
        Equal(false, (bool)shouldReturn.Invoke(null, [TowerResolutionResult.StaleAuthority])!,
            "a technical resolution does not masquerade as defeat extraction");
    }

    private static void CreatureAiUsesRawFriendlyTargetSlots() {
        var aiType = Type("Imlight.CoreLib.Game.Zone.Components.CombatCreatureAIComponent");
        var subCircleType = Type("Imlight.CoreLib.Game.Combat.CombatDuelSubCircle");
        var toRawTargetIndex = aiType.GetMethod("ToRawTargetIndex",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: [subCircleType], modifiers: null)
            ?? throw new InvalidOperationException("Missing CombatCreatureAIComponent.ToRawTargetIndex");
        var subCircleConstructor = subCircleType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 5);
        var color = Activator.CreateInstance(subCircleConstructor.GetParameters()[3].ParameterType);
        object CreateSubCircle(int slotIndex)
            => subCircleConstructor.Invoke([null, 0f, 0f, color, slotIndex]);

        Equal(0U, (uint)toRawTargetIndex.Invoke(null, [CreateSubCircle(0)])!,
            "a boss in combat slot zero targets its own raw slot zero for support spells");
        Equal(3U, (uint)toRawTargetIndex.Invoke(null, [CreateSubCircle(3)])!,
            "friendly support spells retain the raw sigil slot index");
    }

    private static void TowerTurnsAlwaysStartWithThePlayerAndAlternateAfterResolution() {
        var policyType = Type("Imlight.CoreLib.Game.Tower.TowerCombatTurnPolicy");
        var initialTeam = policyType.GetProperty("InitialTeam", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing TowerCombatTurnPolicy.InitialTeam");
        var nextTeam = policyType.GetMethod("NextTeam", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: [typeof(int)], modifiers: null)
            ?? throw new InvalidOperationException("Missing TowerCombatTurnPolicy.NextTeam");
        var canAct = policyType.GetMethod("CanAct", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: [typeof(int), typeof(int)], modifiers: null)
            ?? throw new InvalidOperationException("Missing TowerCombatTurnPolicy.CanAct");

        const int player = 0;
        const int monster = 1;
        Equal(player, (int)initialTeam.GetValue(null)!, "every Tower encounter begins with the player");

        var activeTeam = player;
        for (var action = 0; action < 20; action++) {
            Check((bool)canAct.Invoke(null, [activeTeam, activeTeam])!, "the active team can submit its action");
            Check(!(bool)canAct.Invoke(null, [activeTeam, activeTeam == player ? monster : player])!,
                "the other team cannot submit before the active action resolves");
            activeTeam = (int)nextTeam.Invoke(null, [activeTeam])!;
            Equal(action % 2 == 0 ? monster : player, activeTeam,
                "completed actions alternate player, enemy, player");
        }
    }

    private static void TowerAdmissionRequiresEveryManifestEnemyAndUniqueActors() {
        var state = TowerRunState.Create("duel-admission", 991, 1001);
        var manifest = state.BuildCurrentManifest();
        var roster = NewRoster(manifest);

        Check(!(bool)Get(roster, "IsComplete"), "empty tower roster is incomplete");
        var expected = (IList)Get(manifest, "Enemies");
        var admit = Method(roster, "AdmitEnemy");
        Check((bool)Method(roster, "AdmitPlayer").Invoke(roster, [0x100UL])!, "tower player can be admitted to the duel roster");
        var firstSlotAdmitted = (bool)admit.Invoke(roster, [manifest.EncounterId, 0, 0xA11UL])!;
        Check(firstSlotAdmitted, "first expected tower enemy can be admitted");
        Check(!(bool)admit.Invoke(roster, [manifest.EncounterId, 0, 0xA12UL])!, "a second actor cannot reuse an admitted slot");
        Check(!(bool)admit.Invoke(roster, ["stale-encounter", 1, 0xA13UL])!, "a stale encounter cannot add an enemy");

        if (expected.Count == 2) {
            Check(!(bool)Get(roster, "IsComplete"), "one of two manifest enemies is not a complete admission");
            Check((bool)admit.Invoke(roster, [manifest.EncounterId, 1, 0xA14UL])!, "the second expected tower enemy can be admitted");
        }

        Check((bool)Get(roster, "IsComplete"), "roster is complete only after every manifest slot is admitted");
        Check(!(bool)admit.Invoke(roster, [manifest.EncounterId, expected.Count, 0xA15UL])!, "an extra enemy outside the manifest is rejected");

        var twoEnemyState = TowerRunState.Create("duel-missing-enemy", 993, 1003);
        twoEnemyState.CurrentFloor = 10;
        var twoEnemyManifest = twoEnemyState.BuildCurrentManifest();
        var incompleteRoster = NewRoster(twoEnemyManifest);
        Check((bool)Method(incompleteRoster, "AdmitPlayer").Invoke(incompleteRoster, [0x300UL])!, "player is admitted for missing-enemy resolution test");
        Check((bool)Method(incompleteRoster, "AdmitEnemy").Invoke(incompleteRoster,
            [twoEnemyManifest.EncounterId, 0, 0xA20UL])!, "one floor 10 enemy is admitted");
        var incompleteResolution = Invoke(incompleteRoster, "CreateResolution", 1L, 1, 1);
        Equal("InvalidSnapshot", Invoke(twoEnemyState, "ApplyResolution", incompleteResolution).ToString(),
            "an unadmitted manifest enemy makes the resolution invalid instead of a floor clear");
    }

    private static void TowerKillsRequireCombatDeathEvidence() {
        var state = TowerRunState.Create("duel-deaths", 992, 1002);
        var manifest = state.BuildCurrentManifest();
        var roster = NewRoster(manifest);
        var enemies = (IList)Get(manifest, "Enemies");
        var admit = Method(roster, "AdmitEnemy");
        Check((bool)Method(roster, "AdmitPlayer").Invoke(roster, [0x200UL])!, "tower player is included in the resolution roster");
        var death = Method(roster, "RecordCombatDeath");

        for (var slot = 0; slot < enemies.Count; slot++) {
            var actorObjectId = (ulong)(0xB00 + slot);
            Check(!(bool)death.Invoke(roster, [slot, actorObjectId])!, "unadmitted actor cannot prove a kill");
            Check((bool)admit.Invoke(roster, [manifest.EncounterId, slot, actorObjectId])!, $"manifest enemy slot {slot} is admitted");
        }

        var resolution = Invoke(roster, "CreateResolution", 1L, 1, 1);
        Check((bool)Get(roster, "IsComplete"), "admission remains complete before kill evidence");
        Check(state.TryBeginEncounter((ulong[])Get(resolution, "AdmittedPlayerIds"),
                (int[])Get(resolution, "AdmittedEnemySlots")),
            "durable combat begins after the full admitted roster is known");
        Equal(0, ((int[])Get(resolution, "CombatDeathEnemySlots")).Length,
            "being absent or unobserved does not create a combat-death proof");
        Equal("EncounterContinues", Invoke(state, "ApplyResolution", resolution).ToString(),
            "a complete living roster cannot clear a floor without combat-death evidence");
        Equal(1, state.CurrentFloor, "no death evidence leaves the run on its current floor");

        for (var slot = 0; slot < enemies.Count; slot++) {
            var actorObjectId = (ulong)(0xB00 + slot);
            Check(!(bool)death.Invoke(roster, [slot, actorObjectId + 100])!, "another actor cannot claim this slot's death");
            Check((bool)death.Invoke(roster, [slot, actorObjectId])!, $"round resolution can record death for slot {slot}");
            Check(!(bool)death.Invoke(roster, [slot, actorObjectId])!, "the same death evidence cannot be counted twice");
        }

        resolution = Invoke(roster, "CreateResolution", 1L, 1, 1);
        var admittedSlots = (int[])Get(resolution, "AdmittedEnemySlots");
        var deathSlots = (int[])Get(resolution, "CombatDeathEnemySlots");
        Equal(enemies.Count, admittedSlots.Length, "resolution carries the full admitted roster");
        Equal(enemies.Count, deathSlots.Length, "resolution carries one death proof per manifest enemy");
        Equal("Victory", Invoke(state, "ApplyResolution", resolution).ToString(),
            "complete admission plus server-recorded combat deaths clears the floor");
    }

    private static void TechnicalResolutionCannotBecomeACombatWinOrLoss() {
        var policyType = Type("Imlight.CoreLib.Game.Tower.TowerDuelOutcomePolicy");
        var classify = policyType.GetMethod("Classify", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing TowerDuelOutcomePolicy.Classify");
        foreach (var result in new[] { "EncounterContinues", "StaleAuthority", "NotCurrentEncounter", "InvalidSnapshot", "RunCompleted", "RunNotActive" }) {
            var classification = classify.Invoke(null, [Enum.Parse(typeof(TowerResolutionResult), result)]).ToString();
            Equal("TechnicalAbort", classification, $"{result} uses a technical abort");
        }
        Equal("Victory", classify.Invoke(null, [TowerResolutionResult.Victory])!.ToString(), "only a committed clear is a player victory");
        Equal("Defeat", classify.Invoke(null, [TowerResolutionResult.Defeat])!.ToString(), "only a committed loss is a player defeat");
    }

    private static void LateTowerInterruptionAfterResolutionIsIgnored() {
        Equal("Ignore", ClassifyInterruption(isTowerEncounter: true, isCurrentEncounter: false,
            terminal: true, intentional: false), "terminal Tower duel ignores late disconnect cleanup");
        Equal("Ignore", ClassifyInterruption(isTowerEncounter: true, isCurrentEncounter: false,
            terminal: false, intentional: false), "Tower duel ignores cleanup after its encounter is no longer current");
        Equal("Ignore", ClassifyInterruption(isTowerEncounter: true, isCurrentEncounter: false,
            terminal: true, intentional: true), "late Tower flee input cannot drain mana after resolution");
    }

    private static void TowerResourceWritesRequireCurrentNonterminalEncounter() {
        Equal(true, CanUpdateTowerResources(isTowerEncounter: true, isCurrentEncounter: true,
            terminal: false), "live Tower encounter may persist combat resources");
        Equal(false, CanUpdateTowerResources(isTowerEncounter: true, isCurrentEncounter: false,
            terminal: false), "stale Tower encounter cannot write resources to the newer manifest");
        Equal(false, CanUpdateTowerResources(isTowerEncounter: true, isCurrentEncounter: true,
            terminal: true), "terminal Tower encounter cannot write resources after resolution");
        Equal(false, CanUpdateTowerResources(isTowerEncounter: false, isCurrentEncounter: false,
            terminal: false), "ordinary duel does not use Tower resource persistence");
    }

    private static void LiveTowerIntentionalFleeRetainsManaDrainPolicy() {
        Equal("IntentionalFlee", ClassifyInterruption(isTowerEncounter: true, isCurrentEncounter: true,
            terminal: false, intentional: true), "intentional live Tower flee remains a combat action");
    }

    private static void LiveTowerDisconnectRemainsTechnicalAbort() {
        Equal("TechnicalAbort", ClassifyInterruption(isTowerEncounter: true, isCurrentEncounter: true,
            terminal: false, intentional: false), "live Tower disconnect or logout aborts the encounter");
    }

    private static void OrdinaryDisconnectKeepsNativeFleeBehavior() {
        Equal("NativeFlee", ClassifyInterruption(isTowerEncounter: false, isCurrentEncounter: false,
            terminal: false, intentional: false), "ordinary duel disconnect keeps native flee behavior");
        Equal("IntentionalFlee", ClassifyInterruption(isTowerEncounter: false, isCurrentEncounter: false,
            terminal: false, intentional: true), "ordinary intentional flee remains a flee");
    }

    private static string ClassifyInterruption(bool isTowerEncounter, bool isCurrentEncounter,
        bool terminal, bool intentional) {
        var policyType = Type("Imlight.CoreLib.Game.Tower.TowerDuelInterruptionPolicy");
        var classify = policyType.GetMethod("Classify", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: [typeof(bool), typeof(bool), typeof(bool), typeof(bool)], modifiers: null)
            ?? throw new InvalidOperationException("Missing TowerDuelInterruptionPolicy.Classify");
        return classify.Invoke(null, [isTowerEncounter, isCurrentEncounter, terminal, intentional])!.ToString();
    }

    private static bool CanUpdateTowerResources(bool isTowerEncounter, bool isCurrentEncounter, bool terminal) {
        var policyType = Type("Imlight.CoreLib.Game.Tower.TowerDuelInterruptionPolicy");
        var policy = policyType.GetMethod("CanUpdateResources", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: [typeof(bool), typeof(bool), typeof(bool)], modifiers: null)
            ?? throw new InvalidOperationException("Missing TowerDuelInterruptionPolicy.CanUpdateResources");
        return (bool)(policy.Invoke(null, [isTowerEncounter, isCurrentEncounter, terminal])
            ?? throw new InvalidOperationException("Tower resource policy returned null"));
    }

    private static void InterruptedEncounterConsumesOneRecoveryAndRestoresEntryResources() {
        var state = TowerRunState.Create("duel-recovery", 994, 1004);
        var firstManifest = state.BuildCurrentManifest();
        Check(state.CaptureFloorEntryResource(0x400UL, 88, 36), "first admission saves floor-entry HP and mana");
        Check(state.TryBeginEncounter([0x400UL], Enumerable.Range(0, firstManifest.Enemies.Count).ToArray()),
            "the interruption fixture must persist an active encounter first");
        Equal("InCombat", state.EncounterLifecycle.ToString(), "the encounter lifecycle records active combat");
        Check(state.ScheduleEncounterRecovery(), "a technical interruption schedules one recovery");
        Equal(0, state.RecoveryAllowanceRemaining, "the interruption consumes the run's recovery replay");
        Check(state.RecoveryResourcesPending, "recovery makes the saved resource snapshot pending");

        var recoveredManifest = state.BuildCurrentManifest();
        Check(firstManifest.EncounterId != recoveredManifest.EncounterId,
            "recovery creates a new encounter identity so the physical room is rebuilt");
        Check(state.MarkRecoveryResourcesRestored(0x400UL), "the wizard can consume the pending resource restoration");
        Check(!state.RecoveryResourcesPending, "the pending restore clears after the wizard is restored");
        var snapshot = state.FloorEntryResources.Single();
        Equal(88, snapshot.Health, "recovery restores floor-entry health");
        Equal(36, snapshot.Mana, "recovery restores floor-entry mana");
        Check(state.TryBeginEncounter([0x400UL], Enumerable.Range(0, recoveredManifest.Enemies.Count).ToArray()),
            "the replayed encounter starts only after recovery preparation");
        Check(!state.ScheduleEncounterRecovery(), "a second technical interruption cannot replay within the same run");
        Equal("Defeated", state.Status.ToString(), "an exhausted replay fails to checkpoint defeat");
    }

    private static object NewRoster(TowerEncounterManifest manifest) {
        var type = Type("Imlight.CoreLib.Game.Tower.TowerEncounterRoster");
        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [manifest], culture: null)
            ?? throw new InvalidOperationException("Could not create TowerEncounterRoster");
    }

    private static object Invoke(object target, string name, params object[] args)
        => Method(target, name).Invoke(target, args)
            ?? throw new InvalidOperationException($"{name} returned null");

    private static MethodInfo Method(object target, string name)
        => target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing {target.GetType().Name}.{name}");

    private static object Get(object target, string name)
        => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? throw new InvalidOperationException($"Missing {target.GetType().Name}.{name}");

    private static Type Type(string name)
        => CoreAssembly.GetType(name) ?? throw new InvalidOperationException($"Missing {name}");

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
