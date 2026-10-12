using System;
using Imlight.CoreLib.Game.Zone.Core;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Canonical paths for Golem Court, the private sanctuary, and the shared combat arena.</summary>
public static class TowerZonePaths {
    public const string EntryZone = "WizardCity/WC_Golem_Tower";
    private const string RoomPrefix = "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_";
    public static readonly Imcodec.Math.Vector3 TomeSpawnLocation = new(0, -45, 0);

    public static string GetRoomPath(long floor) => RoomPrefix + GetPhysicalRoomNumber(floor);

    public static int GetPhysicalRoomNumber(long floor) {
        if (floor < 1) { throw new ArgumentOutOfRangeException(nameof(floor)); }
        return TowerRunState.CombatRoomNumber;
    }

    public static bool IsCombatArenaPath(string path)
        => string.Equals(path, RoomPrefix + TowerRunState.CombatRoomNumber, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldRetireCombatArenaOnSanctuaryTransfer(string cachedZonePath, string destinationZonePath)
        => IsCombatArenaPath(cachedZonePath) && IsSanctuaryPath(destinationZonePath);

    public static bool IsEntryPath(string path)
        => string.Equals(path, EntryZone, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, GetSanctuaryPath(), StringComparison.OrdinalIgnoreCase);

    public static string GetSanctuaryPath() => RoomPrefix + TowerRunState.SanctuaryRoomNumber;
    public static bool IsSanctuaryPath(string path)
        => string.Equals(path, GetSanctuaryPath(), StringComparison.OrdinalIgnoreCase);

    public static bool TryGetRoomNumber(string path, out int roomNumber) {
        roomNumber = 0;
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith(RoomPrefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(path.AsSpan(RoomPrefix.Length), out roomNumber)) {
            roomNumber = 0;
            return false;
        }

        return roomNumber is >= 1 and <= TowerRunState.PhysicalRoomCount;
    }

    public static bool IsRoomPath(string path) => TryGetRoomNumber(path, out _);

    public static bool IsTowerDestination(string path)
        => IsRoomPath(path)
        || string.Equals(path, EntryZone, StringComparison.OrdinalIgnoreCase);
}
