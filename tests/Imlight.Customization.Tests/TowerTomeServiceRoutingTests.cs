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
        }

        Console.WriteLine("PASS: Tower Tome service names route visible rows independently of the client service index");
    }

    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }
}
