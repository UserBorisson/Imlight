using Imlight.CoreLib.Game.Tower;

internal static class TowerWizardDeckTests {
    public static void Run() {
        TowerWizardSpellCandidate[] pool = [
            new(1243857701, "Fire", 1, 2, true),
            new(719065368, "Fire", 1, 5, true),
            new(12, "Ice", 1, 1, true),
            new(13, "Fire", 1, 1, false),
        ];
        var novice = TowerWizardDeckModel.Select("Fire", 1, pool);
        if (novice.Count != 1 || novice[0].TemplateId != 103007158 || novice[0].Copies != 4) {
            throw new Exception("A novice receives four authentic Fire Cat cards, without higher-level, off-school or illegal spells.");
        }
        var malicious = TowerWizardDeckModel.Select("Death", 130,
            [new(203556948, "Death", 1, 1, true)]);
        if (malicious.Any(card => card.TemplateId == 203556948))
            throw new Exception("Mass Feint must be denied even with low native metadata.");
        var history = new TowerClearHistory();
        var iceblade = TowerSpellProgression.Find(2032132496U);
        history.Add(3);
        if (!TowerSpellProgression.EligiblePrimary(iceblade, "Ice", 1, history)
            || TowerSpellProgression.EligibleEnemy(2032132496U, "Ice", 3)
            || !TowerSpellProgression.EligibleEnemy(2032132496U, "Ice", 4))
            throw new Exception("Player milestone and next-floor enemy utility access must match.");
        var strong = TowerSpellProgression.Find(1966685517U);
        if (!TowerSpellProgression.EligibleSecondary(strong, "Fire", 50, [])
            || TowerSpellProgression.EligibleSecondary(strong, "Fire", 49, []))
            throw new Exception("Reviewed native Sun training must respect its explicit level.");
        if (TowerSpellProgression.Find(1635493379U)?.RequiredLevel != 100
            || TowerSpellProgression.Find(1697434154U)?.RequiredLevel != 72)
            throw new Exception("Localized canonical wizard quest aliases must be explicitly gated.");
        foreach (var unlock in TowerSpellProgression.Spells.Where(s => s.SecondaryTrainable && s.RequiredSpellId != 0)) {
            var prerequisite = TowerSpellProgression.Find(unlock.RequiredSpellId);
            if (prerequisite is null || (!prerequisite.SecondaryTrainable
                && unlock.RequiredPrimarySchool != prerequisite.School))
                throw new Exception($"Secondary prerequisite is unreachable: {unlock.Name} requires {prerequisite?.Name}.");
        }
        var apprentice = TowerWizardDeckModel.Select("Fire", 5, pool);
        if (!apprentice.Any(card => card.TemplateId == 1243857701)
            || apprentice.Any(card => card.TemplateId is 719065368 or 12 or 13)) {
            throw new Exception("Native training levels and school identity constrain wizard decks.");
        }
        var largePool = Enumerable.Range(1, 100).Select(id => new TowerWizardSpellCandidate((uint)id, "Fire", 1, 1, true));
        var advanced = TowerWizardDeckModel.Select("Fire", 100, largePool);
        if (advanced.Sum(card => card.Copies) > 64 || advanced.Any(card => card.Copies > 6)
            || !advanced.Any(card => card.TemplateId == 103007158)) {
            throw new Exception("Advanced wizard decks are finite, bounded and retain a usable starter attack.");
        }
        foreach (var school in new[] { "Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance" }) {
            if (TowerWizardDeckModel.Select(school, 1, []).Single().TemplateId == 0) {
                throw new Exception("Every native school has an authentic level-one starter.");
            }
        }
        Console.WriteLine("PASS: finite native wizard decks respect training level, school and supported costs");
    }
}
