using System;
using Imcodec.MessageLayer.Generated;
using Imcodec.Types;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Server policy for the Tower's player-first, alternating action sequence.</summary>
internal static class TowerCombatTurnPolicy {
    internal const int PlayerTeam = 0;
    internal const int MonsterTeam = 1;

    internal static int InitialTeam => PlayerTeam;

    internal static bool CanAct(int activeTeam, int actorTeam) => activeTeam == actorTeam;

    internal static bool CanMaintainHand(bool isPlanning, int actorTeam)
        => isPlanning && actorTeam == PlayerTeam;

    internal static byte EnemyPlanningSeconds() => (byte)Random.Shared.Next(2, 8);

    internal static DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI CreatePlanningUI(
        GID duelId, int actingTeam, int alternateTurn) => new() {
        DuelID = duelId,
        AltTurn = alternateTurn,
        AltTurnTeam = actingTeam,
    };

    internal static int NextTeam(int completedTeam) => completedTeam switch {
        PlayerTeam => MonsterTeam,
        MonsterTeam => PlayerTeam,
        _ => throw new ArgumentOutOfRangeException(nameof(completedTeam), completedTeam, "Unknown duel team."),
    };
}
