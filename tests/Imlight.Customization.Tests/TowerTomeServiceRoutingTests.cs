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
