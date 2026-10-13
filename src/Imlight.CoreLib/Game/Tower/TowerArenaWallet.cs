using System;
using Imcodec.MessageLayer.Generated;
using Newtonsoft.Json.Linq;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Native Blue Arena Tickets (CURRENCY_PVPCUR), distinct from legacy Arena points and events.</summary>
public static class TowerArenaWallet {
    public const string BalanceField = "m_currentPvPCurrency";

    public static int GetBalance(JObject wizard)
        => Math.Max(0, wizard?["GameStats"]?[BalanceField]?.Value<int>() ?? 0);

    /// <summary>Must commit together with the wizard/progression documents in the caller's write transaction.</summary>
    public static int Migrate(JObject wizard, TowerPlayerProgression progression) {
        ArgumentNullException.ThrowIfNull(wizard);
        ArgumentNullException.ThrowIfNull(progression);
        if (wizard["GameStats"] is not JObject stats) { throw new InvalidOperationException("Wizard has no native wallet statistics."); }
        if (progression.ArenaWalletMigrationVersion >= 1) { return GetBalance(wizard); }
        var amount = Math.Max(0, progression.Tickets);
        var balance = checked(GetBalance(wizard) + amount);
        stats[BalanceField] = balance;
        progression.LegacyTicketsConverted = amount;
        progression.ArenaWalletMigrationVersion = 1;
        progression.Tickets = 0;
        return balance;
    }

    public static int Credit(JObject wizard, int amount) {
        if (amount < 0) { throw new ArgumentOutOfRangeException(nameof(amount)); }
        var balance = checked(GetBalance(wizard) + amount);
        ((JObject)wizard["GameStats"]!)[BalanceField] = balance;
        return balance;
    }

    public static bool TrySpend(JObject wizard, int amount, out int balance) {
        if (amount < 0) { throw new ArgumentOutOfRangeException(nameof(amount)); }
        balance = GetBalance(wizard);
        if (balance < amount) { return false; }
        balance -= amount;
        ((JObject)wizard["GameStats"]!)[BalanceField] = balance;
        return true;
    }

    public static WIZARD3_56_PROTOCOL.MSG_UPDATEPVPCURRENCY CreateClientUpdate(int balance) => new() {
        PvPCurrency = balance,
        MaxPvPCurrency = int.MaxValue,
    };
}
