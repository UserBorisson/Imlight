using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;
using Imlight.Common;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

internal static class TowerNativeWizardTests {
    private static readonly Assembly Core = typeof(TowerRunState).Assembly;

    internal static void Run() {
        var rootPath = Path.Combine(Environment.CurrentDirectory,
            "development/assets/V_r806919.Wizard_1_610/Data/GameData/Root.wad");
        if (!File.Exists(rootPath)) throw new InvalidOperationException("Run native Tower asset tests from the W101 workspace root.");
        var logDir = Path.Combine(Path.GetTempPath(), "tower-native-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);
        var config = Path.Combine(logDir, "test.ini");
        File.WriteAllText(config, $"[Logging]\nLogPath={logDir}/test.log\nLogLevel=ERROR\nLogFormat={{Message}}{{NewLine}}\nSeqSinkUrl=http://127.0.0.1:1\n[Character]\nBaseGoldPouch=100000\nMaxLevel=170\n");
        ConfigurationManager.Initialize(config);
        using var stream = File.OpenRead(rootPath);
        var archive = ArchiveParser.Parse(stream) ?? throw new InvalidOperationException("Cannot read installed Root.wad.");
        Require("Imlight.CoreLib.Shared.Resources.RootArchiveLoader")
            .GetField("s_rootWad", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, archive);
        _ = CoreObjectFactory.Instance;
        Activator.CreateInstance(Require("Imlight.CoreLib.Game.Spells.MagicSchools"));
        Activator.CreateInstance(Require("Imlight.CoreLib.Shared.Character.MagicLevelsConfig"));
        var localeType = Require("Imlight.CoreLib.Shared.Resources.Locale");
        Activator.CreateInstance(localeType);
        var getName = localeType.GetMethod("GetEnglishName", [typeof(string)])!;
        var configure = Require("Imlight.CoreLib.Game.Tower.TowerWizardAvatar")
            .GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Static)!;
        var setBase = typeof(ServerWizGameStats).GetMethod("SetBaseStats", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var floor in new[] { 1L, 14L, 65L, 100L, 170L, 200L, 10000L }) {
            var run = TowerRunState.Create("native-assets", 123456, 987654);
            run.CurrentFloor = floor;
            var profile = run.BuildCurrentManifest().Enemies.Single();
            var adapter = (GameObjectTemplate)CoreObjectFactory.GetCoreTemplate(profile.TemplateId);
            var obj = CoreObjectFactory.InitializeCoreObjectBehaviors(new WizClientObject { m_globalID = 987654321UL }, 1);
            configure.Invoke(null, [obj, profile, floor]);
            Check(!obj.m_inactiveBehaviors.OfType<ClientWizPlayerNameBehavior>().Single().m_useRank,
                "AI nameplate suppresses the player rank so the mob-style level line is not duplicated");
            Check(obj.m_inactiveBehaviors.OfType<ClientMagicSchoolBehavior>().Single().m_level == (int)floor,
                "AI nameplate displays logical floor as wizard level independently of capped combat statistics");
            Check(obj.m_templateID == 1, "native player object template");
            Check(obj.m_inactiveBehaviors.All(b => b is not NPCBehavior), "native player schema excludes NPC behavior");
            var expectedName = (string)getName.Invoke(null, [adapter.m_displayName.ToString()])!;
            Check(obj.m_inactiveBehaviors.OfType<ClientWizPlayerNameBehavior>().Single().m_wsNameOverride.ToString() == expectedName + $"\n<image;{profile.SchoolId}SchoolIcon> Level {floor}",
                "opponent player name matches the installed Tome floor label");
            var stats = new ServerWizGameStats(Enum.Parse<MagicSchool>(profile.SchoolId), profile.Level);
            setBase.Invoke(stats, null);
            var schoolType = Require("Imlight.CoreLib.Game.Spells.MagicSchools");
            var school = (MagicSchoolTemplate)schoolType.GetMethod("GetMagicSchool", BindingFlags.NonPublic | BindingFlags.Static,
                null, [typeof(string)], null)!.Invoke(null, [profile.SchoolId])!;
            TowerWizardStatModel.Apply(stats, (int)school.m_schoolIndex, profile.HealthMultiplier);
            Check(stats.m_currentHitpoints > 0 && stats.m_currentHitpoints == stats.m_baseHitpoints, "native player health and simulated equipment");
            obj.m_gameStats = stats.GetCombatGameStats();
            var codec = new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None);
            Check(codec.Serialize(obj, PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var payload)
                && ((byte[])payload).Length > 0, "full native player object serializes with production render flags");
            var circleType = Require("Imlight.CoreLib.Game.Combat.CombatDuelSubCircle");
            var circle = RuntimeHelpers.GetUninitializedObject(circleType);
            circleType.GetProperty("CombatParticipant", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(circle,
                new CombatParticipant { m_pGameStats = stats.GetCombatGameStats() });
            var hits = Require("Imlight.CoreLib.Game.Combat.CombatResolver").GetMethod("SpellHits", BindingFlags.NonPublic | BindingFlags.Static)!;
            Check((bool)hits.Invoke(null, [circle, new Spell { m_accuracy = 100, m_magicSchoolID = StringHash.Compute(profile.SchoolId) }])!,
                "native enemy accuracy resolution completes with sparse school stats");
        }
        TowerNativeItemUpgradeTests.Run();
        Console.WriteLine("PASS: installed native player avatar schema, Tome names, floor-tier statistics, render serialization and enemy accuracy resolution");
    }

    private static System.Type Require(string name) => Core.GetType(name) ?? throw new InvalidOperationException($"Missing {name}");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
