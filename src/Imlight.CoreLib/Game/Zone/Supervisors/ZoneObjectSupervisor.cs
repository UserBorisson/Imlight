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
using System.Collections.Generic;
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
    private string _victoryPresentationEncounterId;
    private NodeObject _towerPathNodeSeed;

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        // We only care about the ZoneData section of the message.
        var zoneData = message.ZoneData;
        _towerPathNodeSeed = message.NodeData?.m_nodeList?.FirstOrDefault();
        if (Zone.IsEnabledTowerRoom && !TowerZonePolicy.IsValidManifest(Zone.TowerRunState, Zone.ZonePath)) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS {
                SupervisorName = nameof(ZoneObjectSupervisor),
                Error = true,
                ErrorMessage = "Tower room has no valid, authoritative manifest.",
            });
            return;
        }
        var towerSpawns = Zone.IsEnabledTowerRoom && !TowerZonePaths.IsSanctuaryPath(Zone.ZonePath)
            ? Zone.TowerRunState.CurrentManifest.Enemies
            : null;
        var profiledSlots = new HashSet<int>();
        var initializedSlots = new HashSet<int>();
        var tomeInitialized = !Zone.IsEnabledTowerRoom || !TowerZonePaths.IsSanctuaryPath(Zone.ZonePath);
        var towerSpawnPlan = towerSpawns is null
            ? []
            : TowerEnemySpawnPlanner.Build(
                message.SpawnData,
                message.PathData,
                message.NodeData,
                Zone.TowerRunState.CurrentManifest.EncounterId,
                towerSpawns.Count,
                towerSpawns[0].TemplateId,
                templateId => IsTowerEnemy(CoreObjectFactory.GetCoreTemplate(templateId) as GameObjectTemplate));

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

            // The manifest roster is recreated from this room's authored spawn data below.
            // Never let placed legacy duelists leak into the managed Tower encounter.
            if (towerSpawns is not null && IsTowerEnemy(template)) {
                continue;
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

            CreateEntityActor(coreObject, template, objectInfo);
        }

        if (Zone.IsEnabledTowerRoom && TowerZonePaths.IsSanctuaryPath(Zone.ZonePath)) {
            const uint tomeTemplateId = 1233879; // Root.wad WC_Pedestal_JewelTome.xml
            var tomeTemplate = CoreObjectFactory.GetCoreTemplate(tomeTemplateId) as GameObjectTemplate;
            if (tomeTemplate is null) {
                Logger.Error("Tower sanctuary could not load the installed Tome template {TemplateId}.",
                    Logger.Args(tomeTemplateId));
            }
            else {
                var tomeInfo = new CoreObjectInfo {
                    m_templateID = tomeTemplateId,
                    m_zoneTag = "InfiniteGolemTowerTome",
                    m_location = TowerZonePaths.TomeSpawnLocation,
                    m_fScale = 1.0f,
                };
                var tome = CoreObjectFactory.FinalizeCoreObject(tomeInfo, tomeTemplate);
                tome = CoreObjectFactory.InitializeCoreObjectBehaviors(tome, tomeTemplate);
                tomeInitialized = CreateEntityActor(tome, tomeTemplate, tomeInfo) is not null;
            }
        }

        if (towerSpawns is not null) {
            for (var index = 0; index < Math.Min(towerSpawns.Count, towerSpawnPlan.Count); index++) {
                var plan = towerSpawnPlan[index];
                var template = CoreObjectFactory.GetCoreTemplate(plan.Info.m_templateID) as GameObjectTemplate;
                if (!IsNativeTowerBoss(template)) { continue; }

                var coreObject = CoreObjectFactory.FinalizeCoreObject(plan.Info, template);
                if (coreObject is null) { continue; }
                coreObject = CoreObjectFactory.InitializeCoreObjectBehaviors(coreObject, template);
                if (coreObject is null) { continue; }

                var profile = towerSpawns[index];
                // Client template 1 is the actual player wizard body. Keep the existing server
                // combat adapter solely for proximity, deck and AI components.
                coreObject.m_templateID = 1;
                Zone.RegisterTowerEnemyProfile(coreObject.m_globalID, profile);
                profiledSlots.Add(profile.RosterSlot);
                var creatureActor = CreateEntityActor(coreObject, template, plan.Info, isTowerWizard: true);
                if (creatureActor is not null) {
                    creatureActor.Tell(new ZONE_102_PROTOCOL.MSG_PATHDETAILS {
                        NodeObjects = [.. plan.Nodes],
                    });
                    initializedSlots.Add(profile.RosterSlot);
                }
            }
        }

        if (towerSpawns is not null && towerSpawnPlan.Count < towerSpawns.Count) {
            Logger.Warning("Tower room {ZonePath} resolved {ResolvedCount} usable spawn-data enemies for {ManifestCount} manifest enemies.",
                Logger.Args(Zone.ZonePath, towerSpawnPlan.Count, towerSpawns.Count));
        }

        // A partially built encounter is never playable, even if some authored objects loaded.
        var failedRoster = towerSpawns is not null
            && !TowerZonePolicy.IsCompleteManifestRoster(towerSpawns.Count, profiledSlots, initializedSlots);
        var failedTome = !tomeInitialized;
        // Inform the zone that we have finished initializing all objects.
        var reply = new ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS {
            SupervisorName = nameof(ZoneObjectSupervisor),
            Error = failedRoster || failedTome,
            ErrorMessage = failedTome ? "Tower sanctuary Tome did not initialize."
                : failedRoster ? "Incomplete Tower manifest roster during room initialization." : null,
        };
        Sender.Tell(reply);
    }

    [MessageHandler(typeof(TowerVictoryPresentationSpawnMessage))]
    private void ReceiveTowerVictoryPresentation(TowerVictoryPresentationSpawnMessage message) {
        if (_victoryPresentationEncounterId == message.EncounterId
            || !TowerVictoryPresentationPlanner.CanPresentVictory(
                Zone.TowerRunState, Zone.ZonePath, message.EncounterId, message.Floor)) {
            return;
        }

        if (message.TemplateId != 1) {
            var template = CoreObjectFactory.GetCoreTemplate(message.TemplateId) as GameObjectTemplate;
            var presentationTemplate = TowerVictoryPresentationPlanner.CreateDazedTemplate(template);
            if (presentationTemplate is null) {
                Logger.Warning("Tower victory presentation could not resolve native NPC template {TemplateId}.",
                    Logger.Args(message.TemplateId));
                return;
            }

            var presentationInfo = new CoreObjectInfo {
                m_templateID = message.TemplateId,
                m_zoneTag = $"TowerDazed_{message.Floor}_{message.EncounterId}",
                m_location = message.Location,
                m_orientation = message.Orientation,
                m_fScale = message.Scale,
            };
            var presentation = CoreObjectFactory.FinalizeCoreObject(presentationInfo, presentationTemplate);
            presentation = CoreObjectFactory.InitializeCoreObjectBehaviors(presentation, presentationTemplate);
            var presentationActor = CreateEntityActor(presentation, presentationTemplate, presentationInfo,
                isTowerDazedPresentation: true);
            if (presentationActor is null) { return; }

            presentationActor.Tell(new TowerDazedEnterStateMessage());
        }
        if (_towerPathNodeSeed is null) {
            Logger.Warning("Tower victory wisps could not find an authored arena path node.");
            _victoryPresentationEncounterId = message.EncounterId;
            return;
        }

        foreach (var placement in TowerVictoryPresentationPlanner.BuildWispPlacements(
            TowerZonePaths.TomeSpawnLocation, _towerPathNodeSeed)) {
            var wispTemplate = CoreObjectFactory.GetCoreTemplate(placement.TemplateId) as GameObjectTemplate;
            if (wispTemplate is null) {
                Logger.Warning("Tower victory could not resolve native wisp template {TemplateId}.",
                    Logger.Args(placement.TemplateId));
                continue;
            }

            var info = new CoreObjectInfo {
                m_templateID = placement.TemplateId,
                m_zoneTag = $"TowerWisp_{placement.TemplateId}_{message.EncounterId}",
                m_location = placement.SpawnLocation,
                m_orientation = message.Orientation,
                m_fScale = 1.0f,
            };
            var wisp = CoreObjectFactory.FinalizeCoreObject(info, wispTemplate);
            wisp = CoreObjectFactory.InitializeCoreObjectBehaviors(wisp, wispTemplate);
            var wispActor = CreateEntityActor(wisp, wispTemplate, info);
            wispActor?.Tell(new ZONE_102_PROTOCOL.MSG_PATHDETAILS {
                NodeObjects = [.. placement.Path],
            });
        }

        _victoryPresentationEncounterId = message.EncounterId;
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

    private static bool IsNativeTowerBoss(GameObjectTemplate template)
        => IsTowerEnemy(template)
            && (TowerContentCatalog.IsWizardTemplate(template.m_templateID)
                || template.m_behaviors.OfType<NPCBehaviorTemplate>().Any(behavior => behavior.m_bossMob));

    private void RegisterCriticalObject(GID id) {
        var msg = new ZONE_102_PROTOCOL.MSG_REGISTERCRITICALOBJECT {
            ObjectID = id
        };

        base.ZoneRef.Tell(msg);
    }

}
