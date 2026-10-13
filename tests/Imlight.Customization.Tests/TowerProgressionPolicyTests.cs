using System.Reflection;
using Imlight.CoreLib.Game.Tower;
using Newtonsoft.Json.Linq;

internal static class TowerProgressionPolicyTests {
    internal static void Run() {
        var assembly = typeof(TowerRunState).Assembly;
        var historyType = assembly.GetType("Imlight.CoreLib.Game.Tower.TowerClearHistory")
            ?? throw new InvalidOperationException("Missing compact permanent clear history");
        var history = Activator.CreateInstance(historyType)!;
        var add = historyType.GetMethod("Add")!;
        var contains = historyType.GetMethod("Contains")!;
        foreach (var floor in new long[] { 1, 3, 2, 5, 4, long.MaxValue }) { add.Invoke(history, [floor]); }
        Check((bool)contains.Invoke(history, [3L])!, "a merged clear remains recorded");
        Check(!(bool)contains.Invoke(history, [6L])!, "highest floor does not imply an unseen clear");
        Check((bool)contains.Invoke(history, [long.MaxValue])!, "maximum logical floor does not overflow");
        var ranges = (System.Collections.ICollection)historyType.GetProperty("Ranges")!.GetValue(history)!;
        Check(ranges.Count == 2, "adjacent clears merge into compact ranges while sparse clears remain distinct");

        var wallet = assembly.GetType("Imlight.CoreLib.Game.Tower.TowerArenaWallet")
            ?? throw new InvalidOperationException("Missing native Blue Arena Ticket wallet");
        var migrate = wallet.GetMethod("Migrate", BindingFlags.Public | BindingFlags.Static)!;
        var wizard = JObject.Parse("{\"GameStats\":{\"m_currentPvPCurrency\":100,\"m_currentArenaPoints\":70,\"m_currentEventCurrency1\":1234}}");
        var progress = new TowerPlayerProgression { WizardId = 123, Tickets = 36 };
        Check((int)migrate.Invoke(null, [wizard, progress])! == 136, "remaining custom Tickets add to existing native blue balance");
        Check((int)migrate.Invoke(null, [wizard, progress])! == 136, "migration retries do not double-credit");
        Check(progress.Tickets == 0, "migrated custom balance cannot remain spendable");
        Check(wizard["GameStats"]!["m_currentArenaPoints"]!.Value<int>() == 70, "legacy Arena points remain separate");
        Check(wizard["GameStats"]!["m_currentEventCurrency1"]!.Value<int>() == 1234, "event currency is not used as the new wallet");

        var overflowWizard = JObject.Parse("{\"GameStats\":{\"m_currentPvPCurrency\":2147483647}}");
        var overflowProgress = new TowerPlayerProgression { Tickets = 1 };
        try { migrate.Invoke(null, [overflowWizard, overflowProgress]); throw new InvalidOperationException("overflow must not silently clip Ticket value"); }
        catch (TargetInvocationException ex) when (ex.InnerException is OverflowException) { }
        Check(overflowProgress.Tickets == 1, "failed overflow migration preserves unconverted value");
        Check(TowerDifficultyModel.GetVictoryTickets(1, TowerChallengeMode.Normal) == 3, "floor-1 replay pays three native Tickets");
        Check(TowerDifficultyModel.GetVictoryTickets(11, TowerChallengeMode.Normal) == 6, "floor-11 replay uses reward band two");
        Check(TowerDifficultyModel.GetVictoryTickets(long.MaxValue, TowerChallengeMode.Normal)
            == TowerDifficultyModel.GetVictoryTickets(TowerDifficultyModel.MaximumLevel, TowerChallengeMode.Normal),
            "post-cap farming rewards stop growing with capped enemy statistics");
        var presentation = typeof(TowerSpellProgression).Assembly.GetType("Imlight.CoreLib.Game.Tower.TowerTomePresentation");
        Check(presentation is not null, "Tome needs tested permanent-history and milestone presentation");
        var summary = (string)presentation!.GetMethod("ProgressSummary")!.Invoke(null, ["Fire", 0L, new TowerClearHistory(), Array.Empty<uint>()])!;
        Check(summary.Contains("permanent highest clear 0") && summary.Contains("floor 3"), "Tome distinguishes permanent history and next milestone");
        var delta = (TowerItemStats)presentation.GetMethod("AggregateAfter")!.Invoke(null, [new TowerItemStats(100, 20, .1f, 0, 0, 0), new TowerItemStats(10, 0, .05f, 0, 0, 0), new TowerItemStats(20, 0, .1f, 0, 0, 0)])!;
        Check(delta.Health == 110 && Math.Abs(delta.Damage - .15f) < .00001f, "Aggregate preview applies only the item improvement");
        Console.WriteLine("PASS: compact sparse permanent clears and idempotent native Blue Arena Ticket migration");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
