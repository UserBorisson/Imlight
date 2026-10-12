using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Tower;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>A visible, non-combat replica of the NPC defeated in the Tower encounter.</summary>
internal sealed class TowerDazedEntity(
    CoreObject activeGameObject,
    CoreTemplate template,
    CoreObjectInfo info,
    Akka.Actor.IActorRef zoneRef,
    Zone zone)
    : ZoneEntity(activeGameObject, template, info, zoneRef, zone) {

    [MessageHandler(typeof(TowerDazedEnterStateMessage))]
    private void ReceiveTowerDazedEnterState(TowerDazedEnterStateMessage message) {
        ChangeState("Emoting", new EmoteStateOverrideInfo {
            m_emoteName = "Dazed",
            m_loop = true,
            m_stateNameID = StringHash.Compute("Dazed"),
        });
    }
}
