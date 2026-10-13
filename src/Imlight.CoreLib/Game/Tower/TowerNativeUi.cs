using System.Collections.Concurrent;
using Akka.Actor;
using Imlight.CoreLib.WizardData.Models.Player;
namespace Imlight.CoreLib.Game.Tower;
internal sealed record TowerNativeUiCommand(Wizard Wizard,IActorRef PlayerActor,IActorRef ZoneActor,string Action,string Value);
internal sealed record TowerNativeTrainRequest(Wizard Wizard,IActorRef PlayerActor,int TrainingIndex);
/// <summary>Routes native GUI callbacks only to the same live session and zone that opened the Tome.</summary>
internal static class TowerNativeUi {
    public const uint TrainingJobId=0x544f5701;
    private sealed record Binding(IActorRef PlayerActor,IActorRef ZoneActor,IActorRef TomeActor);
    private static readonly ConcurrentDictionary<ulong,Binding> Bindings=new();
    public static void Bind(ulong wizardId,IActorRef player,IActorRef zone,IActorRef tome)
        => Bindings[wizardId]=new(player,zone,tome);
    public static void Unbind(ulong wizardId,IActorRef player) {
        if(Bindings.TryGetValue(wizardId,out var binding) && Equals(binding.PlayerActor,player))
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<ulong,Binding>>)Bindings)
                .Remove(new(wizardId,binding));
    }
    public static void Send(Wizard wizard,IActorRef player,IActorRef zone,string action,string value="") {
        if(wizard is not null && Bindings.TryGetValue(wizard.CharId,out var binding)
            && Equals(binding.PlayerActor,player) && Equals(binding.ZoneActor,zone))
            binding.TomeActor.Tell(new TowerNativeUiCommand(wizard,player,zone,action,value));
    }
}
