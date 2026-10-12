using System;
using System.Collections.Generic;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Tower;

internal sealed record TowerWispPlacement(uint TemplateId, Vector3 SpawnLocation, NodeObject[] Path);

/// <summary>Builds non-combat victory display objects from the native NPC and wisp assets.</summary>
internal static class TowerVictoryPresentationPlanner {
    internal const uint HealthWispTemplateId = 88014; // Native KT_WispHealth; standard 25% pickup.
    internal const uint ManaWispTemplateId = 88057; // Native WC_WispMana; standard 10% pickup.

    internal static GameObjectTemplate CreateDazedTemplate(GameObjectTemplate source) {
        // The client deserializes a dynamic object using the full native template's behavior schema.
        // Keep the installed template intact; TowerDazedEntity restricts server-side components.
        return source;
    }

    internal static IReadOnlyList<TowerWispPlacement> BuildWispPlacements(
        Vector3 arenaCenter, NodeObject pathNodeSeed) {
        if (pathNodeSeed is null) { return []; }

        var starts = new[] {
            (HealthWispTemplateId, new Vector3(-44, 0, 0)),
            (ManaWispTemplateId, new Vector3(44, 0, 0)),
        };
        var placements = new List<TowerWispPlacement>(starts.Length);
        for (var wispIndex = 0; wispIndex < starts.Length; wispIndex++) {
            var (templateId, offset) = starts[wispIndex];
            var spawn = arenaCenter + offset;
            var locations = new[] {
                spawn,
                spawn + new Vector3(0, 30, 0),
                spawn + new Vector3(26, 30, 0),
                spawn + new Vector3(26, 0, 0),
            };
            var nodes = new NodeObject[locations.Length];
            for (var nodeIndex = 0; nodeIndex < locations.Length; nodeIndex++) {
                var next = locations[(nodeIndex + 1) % locations.Length];
                var location = locations[nodeIndex];
                nodes[nodeIndex] = pathNodeSeed with {
                    m_id = new Imcodec.Types.GID((ulong)((wispIndex + 1) * 16 + nodeIndex + 1)),
                    m_location = location,
                    m_direction = MathF.Atan2(next.Y - location.Y, next.X - location.X),
                };
            }

            placements.Add(new TowerWispPlacement(templateId, spawn, nodes));
        }

        return placements;
    }

    internal static bool CanPresentVictory(TowerRunState run, string zonePath, string encounterId, long floor) {
        if (!TowerZonePaths.IsCombatArenaPath(zonePath)
            || run is null
            || floor < 1
            || !string.Equals(run.LastClearedEncounterId, encounterId, StringComparison.Ordinal)
            || run.HighestFloorCleared != floor
            || run.CurrentManifest is not null
            || run.EncounterLifecycle != TowerEncounterLifecycle.NotStarted) {
            return false;
        }

        return run.Status == TowerRunStatus.Active
                && floor < long.MaxValue
                && run.CurrentFloor == floor + 1
                && run.PendingFloorAdvance
            || run.Status == TowerRunStatus.Completed
                && run.CurrentFloor == floor;
    }
}
