using System.Reflection;
using Imlight.CoreLib.Game.Gifts;
using Akka.Actor;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;

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
        var refreshMode=nativeTraining!.GetField("ImmediateRefresh");
        Equal(true,refreshMode is not null,"native trainer uses the audited options refresh mode");
        Equal(4,(int)refreshMode!.GetValue(null)!,"native mode 4 forces reopening for trainer service types");
        var nativeOptions=((IEnumerable<Imcodec.ObjectProperty.TypeCache.WizTrainingOption>)nativeTraining!.GetMethod("CreateOptions")!.Invoke(null,["Fire","Ice",1,new uint[0]])!).ToArray();
        Equal(true,nativeOptions.Length>0 && nativeOptions.All(o=>o.m_serviceName=="WizTrainingService" && o.m_trainingCost==0),"native school overlay uses free native training options");
        var beetle=nativeOptions.Single(o=>o.m_spellName.ToString() == Imlight.CoreLib.Game.Tower.TowerSpellProgression.Find(1496157882).NativeSpellName);
        Equal(true,beetle.m_bCanTrain,"eligible secondary starter is trainable without Training Points");
        VerifyNativeActorForwarding();
        var learnedOptions=((IEnumerable<Imcodec.ObjectProperty.TypeCache.WizTrainingOption>)nativeTraining.GetMethod("CreateOptions")!
            .Invoke(null,["Fire","Ice",1,new uint[]{1496157882}])!).ToArray();
        Equal(false,learnedOptions.Any(o=>o.m_spellName.ToString()==beetle.m_spellName.ToString()),
            "native trainer hides spells already learned by this wizard");
        var selectInitial=nativeTraining.GetMethod("SelectInitialSchool");
        Equal(true,selectInitial is not null,"fully trained primary school does not hide access to secondary training");
        var allPrimary=Imlight.CoreLib.Game.Tower.TowerSpellProgression.Spells.Where(s=>s.School=="Fire").Select(s=>s.TemplateId).ToArray();
        Equal(false,"Fire"==(string)selectInitial!.Invoke(null,["Fire",170,allPrimary])!,
            "initial tab falls back to an unlearned school when primary inventory is empty");
        var hub=nativeTraining.GetMethod("ComposeHubOptions");
        Equal(true,hub is not null,"Tome initially advertises native training beside its other services");
        var equipmentRow=new Imcodec.ObjectProperty.TypeCache.ServiceOptionBase {m_serviceName="TowerTomeService_Floors_OpenGear"};
        var hubOptions=((IEnumerable<Imcodec.ObjectProperty.TypeCache.ServiceOptionBase>)hub!.Invoke(null,
            [new[]{equipmentRow},nativeOptions])!).ToArray();
        Equal(nativeOptions.Length,hubOptions.OfType<Imcodec.ObjectProperty.TypeCache.WizTrainingOption>().Count(),
            "native trainer inventory is present before the service is selected");
        Equal(true,ReferenceEquals(equipmentRow,hubOptions[0]),"initial native training preserves ordinary Tome actions and indices");
        Equal(false,hubOptions.Any(o=>o.m_serviceName.ToString()=="TowerTomeService_Floors_OpenSpells"),
            "initial training does not depend on a custom submenu swap");
        serializer.Serialize(new Imcodec.ObjectProperty.TypeCache.ServiceMementoBase {m_serviceOptions=hubOptions.ToList()},4,out var hubBytes);
        serializer.Deserialize<Imcodec.ObjectProperty.TypeCache.ServiceMementoBase>(hubBytes,4,out var decodedHub);
        Equal(nativeOptions.Length,decodedHub.m_serviceOptions.OfType<Imcodec.ObjectProperty.TypeCache.WizTrainingOption>().Count(),
            "initial mixed-service NPC memento retains actual native trainer types on the wire");
        var parse=CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerNativeUi")!.GetMethod("ParseClientCommand");
        Equal(true,parse is not null,"native quoted callbacks have a dedicated Tower parser");
        foreach(var command in new[]{"towerui school Ice","\"towerui school Ice\""}) {
            var parsed=(string[])parse!.Invoke(null,[command])!;
            Equal("school",parsed[0],"native callback action parses across the Lua quoting boundary");
            Equal("Ice",parsed[1],"native callback preserves reviewed school identity");
        }
        Equal("opened",((string[])parse!.Invoke(null,["\"towerui opened\""])!)[0],
            "native OnTrainingOpened handshake parses through the quoted client transport");
        foreach(var invalid in new[]{"\"towerui school Ice", "towerui school Ice extra", "towerui school Unknown", "admin reset", "towerui close extra"})
            Equal(null,parse!.Invoke(null,[invalid]),"Tower parser rejects malformed or unrelated commands");
        VerifyTransientMenuResetsOnPlayerLeave();

        Console.WriteLine("PASS: Tower Tome service names route visible rows independently of the client service index");
    }

    private static void VerifyNativeActorForwarding() {
        using var system=ActorSystem.Create("TowerNativeForwardingTests");
        try {
            var entity=system.ActorOf(Props.Create(()=>new ForwardingEntity()));
            var ui=CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerNativeUiCommand")!;
            var train=CoreAssembly.GetType("Imlight.CoreLib.Game.Tower.TowerNativeTrainRequest")!;
            foreach(var message in new[]{
                Activator.CreateInstance(ui,[null,ActorRefs.Nobody,ActorRefs.Nobody,"school","Ice"])!,
                Activator.CreateInstance(train,[null,ActorRefs.Nobody,0])!
            }) {
                Equal("forwarded",entity.Ask<string>(message,TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(),
                    "actual ZoneEntity forwards native Tower UI and Train records to its component");
            }
        } finally { system.Terminate().GetAwaiter().GetResult(); }
    }

    public sealed class ForwardingEntity : ZoneEntity {
        public ForwardingEntity() : base(null,null,null,ActorRefs.Nobody,null) {
            AddComponent(typeof(ForwardingComponent));
        }
    }
    public sealed class ForwardingComponent(ZoneEntity entity) : ZoneEntityComponent(entity) {
        [MessageHandler(typeof(IServerMessage))]
        private void Echo(IServerMessage message) {
            if(message.GetType().Name is "TowerNativeUiCommand" or "TowerNativeTrainRequest") Sender.Tell("forwarded");
        }
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
