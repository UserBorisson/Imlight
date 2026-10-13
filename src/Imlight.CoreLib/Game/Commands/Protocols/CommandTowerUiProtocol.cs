using Imlight.CoreLib.Game.Tower;
namespace Imlight.CoreLib.Game.Commands.Protocols;
/// <summary>Native GUI-script callbacks. Authorization remains the active Tower interaction and durable policies.</summary>
internal sealed class CommandTowerUiProtocol : CommandProtocol {
    internal override string Group {get;set;}="towerui";
    [Command("school")]
    private void School(string school) => TowerNativeUi.Send(Context.Character,Context.SessionActor,Context.ZoneActor,"school",school);
    [Command("close")]
    private void Close() => TowerNativeUi.Send(Context.Character,Context.SessionActor,Context.ZoneActor,"close");
}
