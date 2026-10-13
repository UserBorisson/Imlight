using System.Runtime.CompilerServices;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Behaviors;

internal static class TowerWizardStatsTests {
    public static void Run() {
        var novice = NativeStats(1, 500, 10, 0f);
        TowerWizardStatModel.Apply(novice, 2, 1f);
        Equal(213, novice.m_baseHitpoints, "novice health uses the reduced native wizard baseline without duplicate equipment health");
        Equal(10, novice.m_baseMana, "novice equipment does not add unlimited mana");
        Equal(0f, novice.m_dmgBonusPercent[2], "novice equipment does not grant endgame damage");
        Equal(0f, novice.m_dmgReducePercentAll, "novice equipment does not grant endgame resistance");

        var veteran = NativeStats(100, 2000, 100, .75f);
        TowerWizardStatModel.Apply(veteran, 2, 1f);
        Equal(1100, veteran.m_baseHitpoints, "veteran health uses the approved 110 percent native baseline");
        Equal(.9f, veteran.m_dmgBonusPercent[2], "damage belongs to the native school");
        Equal(0f, veteran.m_dmgBonusPercent[1], "simulated equipment does not grant off-school damage");
        Equal(.2f, veteran.m_dmgReducePercentAll, "veteran equipment grants bounded resistance");
        Equal(1f, veteran.m_powerPipBase + veteran.m_powerPipBonusPercentAll, "power-pip chance never exceeds 100 percent");
        Equal((byte)1, veteran.m_startingPowerPips, "veteran simulated wand grants one starting power pip");

        var scaled = NativeStats(1, 500, 10, 0f);
        TowerWizardStatModel.Apply(scaled, 2, 1.5f);
        Equal(319, scaled.m_baseHitpoints, "Scaled Challenge applies its explicit health modifier to the reduced baseline");
        var otherSchool = NativeStats(1, 300, 10, 0f);
        TowerWizardStatModel.Apply(otherSchool, 3, 1f);
        Equal(128, otherSchool.m_baseHitpoints, "native school health differences are preserved");
        var endless = NativeStats(170, int.MaxValue, 100, .9f);
        TowerWizardStatModel.Apply(endless, 2, 1_000_000f);
        Equal(int.MaxValue, endless.m_baseHitpoints, "endless health arithmetic saturates safely");
        Console.WriteLine("PASS: native wizard statistics and bounded simulated equipment");
    }

    private static ServerWizGameStats NativeStats(int level, int health, int mana, float pipChance) {
        var stats = (ServerWizGameStats)RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.MagicSchool = MagicSchool.Fire;
        stats.Level = level;
        stats.m_baseHitpoints = health;
        stats.m_baseMana = mana;
        stats.m_powerPipBase = pipChance;
        return stats;
    }
    private static void Equal<T>(T expected, T actual, string message) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
