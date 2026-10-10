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
 *
 * ========================================================================
 * CUSTOMIZATION INTEGRATION TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * Exercises configured listener binding through the real actor and sockets.
 * 
 * USAGE EXAMPLE:
 * Run this executable with dotnet run to verify local binding.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nikola Borissov with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;

GiftWireCodecTests.Run();
if (args.Contains("--gift-codec")) { return; }

var directory = Path.Combine(Path.GetTempPath(), "imlight-tests-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
var config = Path.Combine(directory, "test.ini");
File.WriteAllText(config, $"[Game Server]\nTcpBindAddress=127.0.0.2\n[Logging]\nLogPath={directory}/test.log\nLogLevel=INFO\nLogFormat={{Message}}{{NewLine}}\nSeqSinkUrl=http://127.0.0.1:1\n");
ConfigurationManager.Initialize(config);
var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start();
var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using var system = ActorSystem.Create("CustomizationTests");
var listener = system.ActorOf(TcpListenerActor.Props("loopback-test", port, ActorRefs.Nobody));
try {
    IPEndPoint endpoint = null;
    for (var attempt = 0; attempt < 100; attempt++) {
        endpoint = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().FirstOrDefault(item => item.Port == port);
        if (endpoint is not null) { break; }
        await Task.Delay(50);
    }
    if (endpoint is null || !endpoint.Address.Equals(IPAddress.Parse("127.0.0.2"))) {
        throw new Exception($"Configured listener must bind to 127.0.0.2, got {endpoint}");
    }
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Parse("127.0.0.2"), port);
    Console.WriteLine("PASS: configured loopback address accepts a local connection");
} finally {
    await system.Terminate();
}
