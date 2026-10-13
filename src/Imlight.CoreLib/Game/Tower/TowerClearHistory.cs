using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.Game.Tower;

public sealed class TowerClearRange {
    public long First { get; set; }
    public long Last { get; set; }
}

/// <summary>Exact sparse permanent history; consecutive floors take one range.</summary>
public sealed class TowerClearHistory {
    public List<TowerClearRange> Ranges { get; set; } = [];

    public bool Contains(long floor) {
        if (floor < 1) { return false; }
        var low = 0;
        var high = Ranges.Count - 1;
        while (low <= high) {
            var middle = low + (high - low) / 2;
            var range = Ranges[middle];
            if (floor < range.First) { high = middle - 1; }
            else if (floor > range.Last) { low = middle + 1; }
            else { return true; }
        }
        return false;
    }

    public bool Add(long floor) {
        if (floor < 1) { throw new ArgumentOutOfRangeException(nameof(floor)); }
        if (Contains(floor)) { return false; }
        var index = 0;
        while (index < Ranges.Count && Ranges[index].Last < floor) { index++; }
        var left = index > 0 && Ranges[index - 1].Last == floor - 1;
        var right = index < Ranges.Count && floor < long.MaxValue && Ranges[index].First == floor + 1;
        if (left && right) {
            Ranges[index - 1].Last = Ranges[index].Last;
            Ranges.RemoveAt(index);
        }
        else if (left) { Ranges[index - 1].Last = floor; }
        else if (right) { Ranges[index].First = floor; }
        else { Ranges.Insert(index, new TowerClearRange { First = floor, Last = floor }); }
        return true;
    }
}
