using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Tower;

internal sealed record TowerEnemySpawnPlanEntry(SpawnObjectInfo Info, NodeObject Node, NodeObject[] Nodes);

/// <summary>
/// Resolves manifest combatants from a Tower room's authored spawn, path, and node data.
/// Legacy path actors remain disabled in managed Tower rooms; this planner creates only the
/// fixed roster owned by the persisted encounter manifest.
/// </summary>
internal static class TowerEnemySpawnPlanner {
    private sealed record Candidate(SpawnObjectInfo Info, NodeObject[] Nodes, int Chance);

    internal static IReadOnlyList<TowerEnemySpawnPlanEntry> Build(
        SpawnManager spawnData,
        PathTemplateList pathData,
        NodeTemplateList nodeData,
        string encounterId,
        int enemyCount,
        Func<Imcodec.Types.GID, bool> isTowerEnemyTemplate) {
        if (enemyCount <= 0 || spawnData?.m_spawners is null
            || pathData?.m_pathList is null || nodeData?.m_nodeList is null
            || isTowerEnemyTemplate is null) {
            return [];
        }

        var candidates = new List<Candidate>();
        foreach (var spawner in spawnData.m_spawners) {
            if (spawner?.m_spawnList is null) { continue; }
            foreach (var item in spawner.m_spawnList) {
                var info = item?.m_objectInfo;
                if (info is null || !isTowerEnemyTemplate(info.m_templateID)) { continue; }

                var path = pathData.m_pathList.FirstOrDefault(candidate => candidate is not null
                    && candidate.m_id.Full == info.m_pathID.Full);
                if (path?.m_nodeIDs is null) { continue; }

                var nodes = path.m_nodeIDs
                    .Select(id => nodeData.m_nodeList.FirstOrDefault(node => node is not null && node.m_id.Full == id.Full))
                    .Where(node => node is not null)
                    .ToArray();
                if (nodes.Length == 0) { continue; }

                candidates.Add(new Candidate(info, nodes, item.m_percentChance));
            }
        }

        if (candidates.Count == 0) { return []; }

        var plan = new List<TowerEnemySpawnPlanEntry>(enemyCount);
        for (var slot = 0; slot < enemyCount; slot++) {
            var seed = StableHash(encounterId, slot);
            var candidate = SelectCandidate(candidates, seed);
            var node = SelectNode(candidate, seed);
            var location = GetRosterLocation(node.m_location, slot, enemyCount);
            var offset = location - node.m_location;
            var orientation = new Vector3(
                candidate.Info.m_orientation.X,
                candidate.Info.m_orientation.Y,
                (float)(node.m_direction * (Math.PI / 180)));
            var info = candidate.Info with {
                m_location = location,
                m_orientation = orientation,
            };
            var translatedNodes = candidate.Nodes
                .Select(pathNode => pathNode with { m_location = pathNode.m_location + offset })
                .ToArray();
            var translatedStartNode = translatedNodes.First(pathNode => pathNode.m_id == node.m_id);
            plan.Add(new TowerEnemySpawnPlanEntry(info, translatedStartNode, translatedNodes));
        }

        return plan;
    }

    private static Candidate SelectCandidate(IReadOnlyList<Candidate> candidates, ulong seed) {
        var totalChance = candidates.Sum(candidate => candidate.Chance);
        if (totalChance <= 0) {
            return candidates[(int)(seed % (ulong)candidates.Count)];
        }

        var choice = (int)(seed % (ulong)totalChance);
        foreach (var candidate in candidates) {
            if (candidate.Chance <= 0) { continue; }
            if (choice < candidate.Chance) { return candidate; }
            choice -= candidate.Chance;
        }

        return candidates[^1];
    }

    private static NodeObject SelectNode(Candidate candidate, ulong seed)
        => candidate.Info.m_kStartNodeType switch {
            StartNodeType.SNT_FIRST => candidate.Nodes[0],
            StartNodeType.SNT_LAST => candidate.Nodes[^1],
            StartNodeType.SNT_SPECIFIC => candidate.Nodes.FirstOrDefault(node => node.m_id.Full == candidate.Info.m_startNode)
                ?? candidate.Nodes[0],
            _ => candidate.Nodes[(int)(seed % (ulong)candidate.Nodes.Length)],
        };

    private static Vector3 GetRosterLocation(Vector3 origin, int slot, int enemyCount) {
        if (enemyCount <= 1) { return origin; }

        // Keep generated combatants around the room's authored encounter point, with stable spacing.
        var ring = slot / 8;
        var radius = 4.5f + (ring * 1.5f);
        var angle = slot * 2.39996323f;
        return origin + new Vector3(
            (float)Math.Cos(angle) * radius,
            (float)Math.Sin(angle) * radius,
            0f);
    }

    private static ulong StableHash(string value, int slot) {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in value ?? string.Empty) {
            hash ^= character;
            hash *= prime;
        }
        hash ^= (uint)slot;
        hash *= prime;
        return hash;
    }
}
