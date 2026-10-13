using System.Reflection;
using Imlight.CoreLib.Game.Gifts;

internal static class TowerTomeServiceRoutingTests {
    private static readonly Assembly CoreAssembly = typeof(SchoolGiftCampaign).Assembly;

    public static void Run() {
        const string spellsService = "TowerTomeService_Floors_OpenSpells";
        string[] visibleServices = [
            "TowerTomeService_Floors_SetMode",
            "TowerTomeService_Floors_SelectHighest",
            spellsService,
            "TowerTomeService_Floors_OpenGear",
            "TowerTomeService_Floors_ResetFloors",
            "TowerTomeService_Floors_SelectFloor_1",
            "TowerTomeService_Floors_SelectFloor_2",
        ];

        var routerType = CoreAssembly.GetType(
            "Imlight.CoreLib.Game.Zone.Components.TowerTomeOptionRouter");
        var resolveMethod = routerType?.GetMethod("ResolveActionIndex",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var selectedIndex = resolveMethod is null
            ? 0
            : Convert.ToInt32(resolveMethod.Invoke(null, [visibleServices, spellsService, 0U]));

        Equal(2, selectedIndex,
            "a click on the Spells row resolves by its service name when the client reports service index zero");

        if (resolveMethod is not null) {
            var unknownIndex = Convert.ToInt32(resolveMethod.Invoke(null, [visibleServices, "UnknownTowerTomeAction", 0U]));
            Equal(-1, unknownIndex, "an unknown Tower Tome service name cannot fall through to Challenge at index zero");
            Equal(4, Convert.ToInt32(resolveMethod.Invoke(null,
                [visibleServices, "TowerTomeService_Floors_ResetFloors", 0U])),
                "Reset routes to its own action when the native client supplies index zero");
        }

        var resetDialog = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerTomeDialogs")?.GetMethod("CreateResetConfirmation");
        Equal(true, resetDialog is not null, "Reset opens a distinct native confirmation dialog");
        var confirmation=(Imcodec.ObjectProperty.TypeCache.ServiceMementoBase)resetDialog!.Invoke(null,["GUI_Unknown", "", new List<Imcodec.ObjectProperty.TypeCache.ServiceOptionBase> {
            new(){m_serviceName="TowerTomeService_ResetConfirmation_ConfirmReset",m_displayKey="GUI_Yes"},
            new(){m_serviceName="TowerTomeService_ResetConfirmation_CancelReset",m_displayKey="GUI_No"},
        }])!;
        Equal(2, confirmation.m_serviceOptions.Count, "confirmation dialog has separate Yes and No actions");
        Equal(true, confirmation.m_personaMadlibs.m_madlibs.OfType<Imcodec.ObjectProperty.TypeCache.MadlibArgT_WideByteString>().Single().m_madlibArgument.ToString().Contains("Are you sure"), "confirmation question is native dialog body text");
        var popup = CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerSpellLearning")!
            .GetMethod("CreateAcquisitionPopup", BindingFlags.Static | BindingFlags.NonPublic);
        Equal(true, popup is not null, "automatic learning emits the native learned-spell reward popup");
        var popupMessage = (Imcodec.MessageLayer.Generated.WIZARD_12_PROTOCOL.MSG_QUESTREWARDS)popup!.Invoke(null, [new uint[]{103007158}])!;
        new Imcodec.ObjectProperty.ObjectSerializer(Versionable:false).Deserialize<Imcodec.ObjectProperty.TypeCache.LootInfoList>(popupMessage.LootList,1,out var rewards);
        Equal(103007158U, rewards.m_loot.OfType<Imcodec.ObjectProperty.TypeCache.AddSpellLootInfo>().Single().m_spellID, "native acquisition popup contains the exact learned spell");
        var serializer = (Imcodec.ObjectProperty.ObjectSerializer)CoreAssembly.GetType("Imlight.CoreLib.Game.Zone.Components.TowerTomeComponent")!
            .GetField("s_serializer", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var memento = new Imcodec.ObjectProperty.TypeCache.ServiceMementoBase {m_npcTextKey="GUI_NPCInteractText",m_serviceOptions=[]};
        serializer.Serialize(memento, 4, out var actual);
        new Imcodec.ObjectProperty.ObjectSerializer(Versionable:false, Behaviors:Imcodec.ObjectProperty.SerializerFlags.None).Serialize(memento,4,out var expected);
        Equal(true, ((byte[])actual).SequenceEqual((byte[])expected), "Tome submenu uses the same unversioned wire format as the working initial NPC menu");
        var nativeTraining=CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerNativeTraining");
        Equal(true,nativeTraining is not null,"Tower opens native training options instead of generic spell rows");
        var reopen=nativeTraining!.GetMethod("CreateReinteract");
        Equal(true,reopen is not null,"native trainer transitions close the current interaction without leaving NPC range");
        var reopenMessage=(Imcodec.MessageLayer.Generated.QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC)reopen!.Invoke(null,[1234UL])!;
        Equal(1234UL,reopenMessage.GlobalID,"native transition retains the Tome interaction target");
        Equal(2,reopenMessage.Reinteract,"native transition requests immediate reinteraction");
        Equal("",reopenMessage.ServiceName.ToString(),"native transition closes the previous service before replacing options");
        var nativeOptions=((IEnumerable<Imcodec.ObjectProperty.TypeCache.WizTrainingOption>)nativeTraining!.GetMethod("CreateOptions")!.Invoke(null,["Fire","Ice",1,new uint[0]])!).ToArray();
        Equal(true,nativeOptions.Length>0 && nativeOptions.All(o=>o.m_serviceName=="WizTrainingService" && o.m_trainingCost==0),"native school overlay uses free native training options");
        var beetle=nativeOptions.Single(o=>o.m_spellName.ToString() == Imlight.CoreLib.Game.Tower.TowerSpellProgression.Find(1496157882).NativeSpellName);
        Equal(true,beetle.m_bCanTrain,"eligible secondary starter is trainable without Training Points");
        VerifyTransientMenuResetsOnPlayerLeave();

        Console.WriteLine("PASS: Tower Tome service names route visible rows independently of the client service index");
    }

    private static void VerifyTransientMenuResetsOnPlayerLeave() {
        const ulong wizardId = 5018148157037337289;
        var stateType = CoreAssembly.GetType(
            "Imlight.CoreLib.Game.Zone.Components.TowerTomeMenuState");
        if (stateType is null) {
            throw new InvalidOperationException("Tome menu-session state exists and can clear per-player submenus on leave");
        }

        var state = Activator.CreateInstance(stateType, nonPublic: true)
            ?? throw new InvalidOperationException("Tome menu-session state can be created");
        var menuType = CoreAssembly.GetType("Imlight.CoreLib.Game.Zone.Components.TowerTomeMenu");
        if (menuType is null) {
            throw new InvalidOperationException("Tome menu state enum exists");
        }

        var gearMenu = Enum.Parse(menuType, "Gear");
        var trackPlayer = stateType.GetMethod("TrackPlayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var setMenu = stateType.GetMethod("SetMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var playerLeft = stateType.GetMethod("PlayerLeft", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var getMenu = stateType.GetMethod("GetMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (trackPlayer is null || setMenu is null || playerLeft is null || getMenu is null) {
            throw new InvalidOperationException("Tome menu state exposes player lifecycle operations");
        }

        trackPlayer.Invoke(state, ["session-actor-1", wizardId]);
        setMenu.Invoke(state, [wizardId, gearMenu]);
        Equal("Gear", getMenu.Invoke(state, [wizardId])?.ToString(),
            "selecting Equipment stores the Gear submenu for the active player");

        playerLeft.Invoke(state, ["session-actor-1"]);
        Equal("Floors", getMenu.Invoke(state, [wizardId])?.ToString(),
            "a persistent sanctuary actor resets the submenu when the player leaves or logs out");
    }

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }
}
