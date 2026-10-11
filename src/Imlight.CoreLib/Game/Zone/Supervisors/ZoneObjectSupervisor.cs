/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imlight.CoreLib.Game.Tower;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

/// <summary>
/// Exists as a child actor of a <see cref="Zone"/> and is the supervisor 
/// for any objects that are created within the zone.
/// <remarks>Initializes any <see cref="CoreObjectInfo"/> found within <see cref="ZoneData.m_objectList"/> field of the
/// given zone data.</remarks>
/// </summary>
/// <param name="zone">The zone that this supervisor is responsible for.</param>
internal sealed class ZoneObjectSupervisor(Core.Zone zone) : ZoneEntitySupervisor(zone) {

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        // We only care about the ZoneData section of the message.
        var zoneData = message.ZoneData;
        var towerSpawns = Zone.TowerRunState?.CurrentManifest?.Enemies;
        var eligibleTowerEnemies = towerSpawns is null
            ? []
            : zoneData.m_objectList
                .Where(IsObjectEligibleForSpawn)
                .Select(info => (Info: info, Template: CoreObjectFactory.GetCoreTemplate(info.m_templateID) as GameObjectTemplate))
                .Where(candidate => IsTowerEnemy(candidate.Template))
                .ToList();
        var assignedTowerEnemies = 0;

        // Initialize any objects found within the zone data.
        foreach (var objectInfo in zoneData.m_objectList) {
            if (!IsObjectEligibleForSpawn(objectInfo)) {
                continue;
            }

            var template = (GameObjectTemplate) CoreObjectFactory.GetCoreTemplate(objectInfo.m_templateID);
            if (template is null) {
                Logger.Warning("Could not create {0} because template ID {1} was not found.",
                    Logger.Args(objectInfo.m_zoneTag, objectInfo.m_templateID));

                continue;
            }

            TowerEnemySpawn towerSpawn = null;
            if (towerSpawns is not null && IsTowerEnemy(template)) {
                if (assignedTowerEnemies >= towerSpawns.Count) {
                    continue; // The manifest is the authority; do not leave original extra mobs in the room.
                }
                towerSpawn = towerSpawns[assignedTowerEnemies++];
            }

            var coreObject = CoreObjectFactory.FinalizeCoreObject(objectInfo, template);
            if (coreObject is null) {
                Logger.Warning("Could not finalize CoreObject {0} with template ID {1}.",
                    Logger.Args(objectInfo.m_zoneTag, objectInfo.m_templateID));

                continue;
            }

            // Determine if this is a critical object.
            // If so, register it with the Zone.
            if (IsCriticalObject(template)) {
                RegisterCriticalObject(coreObject.m_globalID);
            }

            if (towerSpawn is not null) {
                Zone.RegisterTowerEnemyProfile(coreObject.m_globalID, towerSpawn);
            }

            var objectActor = CreateEntityActor(coreObject, template, objectInfo);
        }

        // Some tower rooms have fewer authored duelist objects than the floor 10 manifest needs.
        // Clone the room's first authored enemy with a separated location so every manifest enemy is
        // represented by a real server combatant and can be included in the encounter snapshot.
        if (towerSpawns is not null && assignedTowerEnemies < towerSpawns.Count && eligibleTowerEnemies.Count > 0) {
            var source = eligibleTowerEnemies[0];
            for (var index = assignedTowerEnemies; index < towerSpawns.Count; index++) {
                var duplicateInfo = source.Info with {
                    m_location = source.Info.m_location + new Vector3(120f * (index + 1), 0f, 0f),
                };
                var coreObject = CoreObjectFactory.FinalizeCoreObject(duplicateInfo, source.Template);
                if (coreObject is null) { continue; }
                Zone.RegisterTowerEnemyProfile(coreObject.m_globalID, towerSpawns[index]);
                CreateEntityActor(coreObject, source.Template, duplicateInfo);
                assignedTowerEnemies++;
            }
        }

        if (towerSpawns is not null && (eligibleTowerEnemies.Count == 0 || assignedTowerEnemies < towerSpawns.Count)) {
            Logger.Warning("Tower room {ZonePath} authored {AuthoredCount} usable duelist objects for {ManifestCount} manifest enemies.",
                Logger.Args(Zone.ZonePath, eligibleTowerEnemies.Count, towerSpawns.Count));
        }

        // Inform the zone that we have finished initializing all objects.
        var reply = new ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS { SupervisorName = nameof(ZoneObjectSupervisor) };
        Sender.Tell(reply);
    }

    private static bool IsObjectEligibleForSpawn(CoreObjectInfo objectInfo) {
        if (objectInfo is null) {
            return false;
        }

        // Do not spawn combat sigils within this supervisor.
        if (objectInfo is CombatSigilObjectInfo) {
            return false;
        }

        // Dungeon-entry sigil pads spawn within ZoneSigilSupervisor.
        if (objectInfo is MinigameSigilInfo) {
            return false;
        }

        return true;
    }

    private static bool IsCriticalObject(CoreTemplate template) {
        if (template is not GameObjectTemplate goTemplate) {
            return false;
        }

        // Check if the adjectives contain "Critical."
        var adjectives = goTemplate.m_adjectiveList;
        return adjectives is not null
            && adjectives.Any(adj => adj.Equals("Critical", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTowerEnemy(GameObjectTemplate template)
        => template?.m_behaviors?.Any(behavior => behavior is NPCBehaviorTemplate) == true
        && template.m_behaviors.Any(behavior => behavior is DuelistBehaviorTemplate);

    private void RegisterCriticalObject(GID id) {
        var msg = new ZONE_102_PROTOCOL.MSG_REGISTERCRITICALOBJECT {
            ObjectID = id
        };

        base.ZoneRef.Tell(msg);
    }

}
