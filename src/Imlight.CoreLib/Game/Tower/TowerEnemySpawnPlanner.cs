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
    internal const float MinimumRosterSeparation = 10f;

    private sealed record Candidate(SpawnObjectInfo Info, NodeObject[] Nodes, int Chance);
    private sealed record PlacementChoice(Candidate Candidate, NodeObject StartNode, Vector3 Location);

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

        var choicesBySlot = Enumerable.Range(0, enemyCount)
            .Select(slot => GetPlacementChoices(candidates, encounterId, slot, enemyCount))
            .ToArray();
        var selected = new PlacementChoice[enemyCount];
        if (choicesBySlot.Any(choices => choices.Length == 0)
            || !TrySelectSeparatedRoster(choicesBySlot, selected, slot: 0)) {
            return [];
        }

        var plan = new List<TowerEnemySpawnPlanEntry>(enemyCount);
        for (var slot = 0; slot < selected.Length; slot++) {
            var choice = selected[slot];
            var offset = choice.Location - choice.StartNode.m_location;
            var orientation = new Vector3(
                choice.Candidate.Info.m_orientation.X,
                choice.Candidate.Info.m_orientation.Y,
                (float)(choice.StartNode.m_direction * (Math.PI / 180)));
            var info = choice.Candidate.Info with {
                m_location = choice.Location,
                m_orientation = orientation,
            };
            var translatedNodes = choice.Candidate.Nodes
                .Select(pathNode => pathNode with { m_location = pathNode.m_location + offset })
                .ToArray();
            var translatedStartNode = translatedNodes.First(pathNode => pathNode.m_id == choice.StartNode.m_id);
            plan.Add(new TowerEnemySpawnPlanEntry(info, translatedStartNode, translatedNodes));
        }

        return plan;
    }

    private static PlacementChoice[] GetPlacementChoices(
        IReadOnlyList<Candidate> candidates,
        string encounterId,
        int slot,
        int enemyCount) {
        var seed = StableHash(encounterId, slot);
        var preferredCandidate = SelectCandidate(candidates, seed);
        var totalChance = candidates.Sum(candidate => candidate.Chance);
        var eligibleCandidates = candidates
            .Where(candidate => totalChance <= 0 || candidate.Chance > 0)
            .ToArray();
        var preferredIndex = Array.FindIndex(eligibleCandidates,
            candidate => ReferenceEquals(candidate, preferredCandidate));
        if (preferredIndex < 0) { return []; }

        var choices = new List<PlacementChoice>();
        for (var candidateOffset = 0; candidateOffset < eligibleCandidates.Length; candidateOffset++) {
            var candidate = eligibleCandidates[(preferredIndex + candidateOffset) % eligibleCandidates.Length];
            foreach (var node in GetNodeAlternatives(candidate, seed)) {
                choices.Add(new PlacementChoice(candidate, node,
                    GetRosterLocation(node.m_location, slot, enemyCount)));
            }
        }

        return choices.ToArray();
    }

    private static IReadOnlyList<NodeObject> GetNodeAlternatives(Candidate candidate, ulong seed) {
        var preferredNode = SelectNode(candidate, seed);
        if (candidate.Info.m_kStartNodeType is StartNodeType.SNT_FIRST
            or StartNodeType.SNT_LAST or StartNodeType.SNT_SPECIFIC) {
            return [preferredNode];
        }

        var preferredIndex = Array.FindIndex(candidate.Nodes, node => ReferenceEquals(node, preferredNode));
        if (preferredIndex < 0) { return []; }
        return Enumerable.Range(0, candidate.Nodes.Length)
            .Select(offset => candidate.Nodes[(preferredIndex + offset) % candidate.Nodes.Length])
            .ToArray();
    }

    private static bool TrySelectSeparatedRoster(
        IReadOnlyList<PlacementChoice[]> choicesBySlot,
        PlacementChoice[] selected,
        int slot) {
        if (slot == choicesBySlot.Count) { return true; }

        foreach (var choice in choicesBySlot[slot]) {
            var separated = true;
            for (var previous = 0; previous < slot; previous++) {
                if (!AreSeparated(choice.Location, selected[previous].Location)) {
                    separated = false;
                    break;
                }
            }
            if (!separated) { continue; }

            selected[slot] = choice;
            if (TrySelectSeparatedRoster(choicesBySlot, selected, slot + 1)) { return true; }
        }

        return false;
    }

    private static bool AreSeparated(Vector3 first, Vector3 second) {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return (dx * dx) + (dy * dy) >= MinimumRosterSeparation * MinimumRosterSeparation;
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
