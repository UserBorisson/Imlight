using Imlight.CoreLib.Game.Tower;

internal static class TowerWizardDeckTests {
    public static void Run() {
        TowerWizardSpellCandidate[] pool = [
            new(10, "Fire", 5, 2, true),
            new(11, "Fire", 30, 5, true),
            new(12, "Ice", 1, 1, true),
            new(13, "Fire", 1, 1, false),
        ];
        var novice = TowerWizardDeckModel.Select("Fire", 1, pool);
        if (novice.Count != 1 || novice[0].TemplateId != 103007158 || novice[0].Copies != 4) {
            throw new Exception("A novice receives four authentic Fire Cat cards, without higher-level, off-school or illegal spells.");
        }
        var apprentice = TowerWizardDeckModel.Select("Fire", 5, pool);
        if (!apprentice.Any(card => card.TemplateId == 10)
            || apprentice.Any(card => card.TemplateId is 11 or 12 or 13)) {
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
