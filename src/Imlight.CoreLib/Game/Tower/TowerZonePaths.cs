using System;
using Imlight.CoreLib.Game.Zone.Core;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Canonical paths for the existing Golem Tower court and its five reusable rooms.</summary>
public static class TowerZonePaths {
    public const string EntryZone = "WizardCity/WC_Golem_Tower";
    private const string RoomPrefix = "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_";

    public static string GetRoomPath(int floor) {
        if (floor is < 1 or > TowerRunState.FloorCount) { throw new ArgumentOutOfRangeException(nameof(floor)); }
        var room = ((floor - 1) % TowerRunState.PhysicalRoomCount) + 1;
        return RoomPrefix + room;
    }

    public static bool IsEntryPath(string path)
        => string.Equals(path, EntryZone, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, GetRoomPath(1), StringComparison.OrdinalIgnoreCase);

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
