using Imlight.CoreLib.Game.Tower;

internal static class TowerItemUpgradeTests {
    public static void Run() {
        var starter = TowerItemUpgradeModel.GetTarget("Fire", "Hat", 1);
        var cap = TowerItemUpgradeModel.GetTarget("Fire", "Hat", TowerItemUpgradeModel.MaximumTier);
        if (starter.Health <= 0 || cap.Health <= starter.Health || cap.Damage <= starter.Damage)
            throw new Exception("Starter gear targets must grow to capped school-specific budgets.");
        var strong = new TowerItemStats(99999, 99999, 9, 9, 9, 9);
        if (TowerItemUpgradeModel.Gap(strong, cap) != TowerItemStats.Zero)
            throw new Exception("Strong native stats cannot be doubled or charged for no improvement.");
        var history = new TowerClearHistory();
        if (TowerItemUpgradeModel.MaximumEligibleTier(10, history) != 1)
            throw new Exception("Level alone cannot bypass Tower milestone gates.");
        history.Add(9);
        if (TowerItemUpgradeModel.MaximumEligibleTier(10, history) != 2
            || TowerItemUpgradeModel.MaximumEligibleTier(1, history) != 1
            || TowerItemUpgradeModel.Cost(2) != 20)
            throw new Exception("Upgrade gates require both level and permanent clear; cost is 10B.");
        foreach (var slot in TowerItemUpgradeModel.Slots)
            if (TowerItemUpgradeModel.GetTarget("Life", slot, 1) == TowerItemStats.Zero)
                throw new Exception("Each equipped combat slot needs an ordinary numeric target.");
        Console.WriteLine("PASS: school/slot upgrade budgets, gap fill, cap and permanent progression gates");
    }
}
