using System.Net;
using System.Net.Sockets;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Implementations;
using Raven.Client.Documents;

internal static class EmbeddedDatabaseStartupTests {
    public static void Run() {
        var directory = Path.Combine(Path.GetTempPath(), "imlight-embedded-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = ReservePort();
        var configPath = Path.Combine(directory, "test.ini");
        var logPath = Path.Combine(directory, "test.log");
        File.WriteAllLines(configPath, [
            "[Database]",
            "PlayerDatabaseName = TowerStartupConcurrency",
            "PlayerDatabaseUrl =",
            "PlayerDatabaseCertificatePath =",
            "DatabaseMaxNumberOfRequestsPerSession = 30",
            "DatabaseRequestTimeoutInSeconds = 30",
            "DatabaseWaitForNonStaleResultsTimeout = 10",
            $"EmbeddedDatabaseDataDirectory = {Path.Combine(directory, "data")}",
            "EmbeddedDatabaseUseFull = false",
            "EmbeddedDatabaseFullPath =",
            $"EmbeddedDatabasePort = {port}",
            "EmbeddedDatabaseTimeoutTime = 60",
            "[Logging]",
            $"LogPath = {logPath}",
            "LogLevel = INFO",
            "LogFormat = {Message}{NewLine}",
            "SeqSinkUrl = http://127.0.0.1:1",
        ]);
        ConfigurationManager.Initialize(configPath);

        const int workerCount = 16;
        var stores = new IDocumentStore[workerCount];
        var errors = new Exception[workerCount];
        using var start = new Barrier(workerCount);
        var workers = Enumerable.Range(0, workerCount).Select(index => new Thread(() => {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(30))) {
                errors[index] = new TimeoutException("Concurrent database start barrier timed out.");
                return;
            }

            try {
                stores[index] = index % 2 == 0
                    ? PlayerDatabase.Instance.Store
                    : SecondaryTestDatabase.Instance.Store;
            }
            catch (Exception exception) {
                errors[index] = exception;
            }
        }) { IsBackground = true }).ToArray();

        try {
            foreach (var worker in workers) { worker.Start(); }
            foreach (var worker in workers) {
                if (!worker.Join(TimeSpan.FromMinutes(2))) {
                    throw new TimeoutException("Concurrent database store initialization did not finish.");
                }
            }

            var failures = errors.Where(error => error is not null).Select(error => error.ToString()).ToArray();
            Check(failures.Length == 0, "concurrent store callers complete without exceptions: " + string.Join(" | ", failures));
            Check(stores.All(store => store is not null), "every concurrent caller receives a document store");
            Check(stores.Where((_, index) => index % 2 == 0).All(store => ReferenceEquals(store, stores[0])),
                "callers for the player database share one initialized document store");
            Check(stores.Where((_, index) => index % 2 == 1).All(store => ReferenceEquals(store, stores[1])),
                "callers for the second database share one initialized document store");
            Check(!ReferenceEquals(stores[0], stores[1]), "different database singletons receive separate document stores");
            Check(EmbeddedDatabaseManager.IsRunning, "the embedded RavenDB server is running after initialization");

            Console.WriteLine("PASS: concurrent embedded-store initialization starts one RavenDB server and shares one store per database");
        }
        finally {
            var uniqueStores = new HashSet<IDocumentStore>(ReferenceEqualityComparer.Instance);
            foreach (var store in stores) {
                if (store is not null && uniqueStores.Add(store)) { store.Dispose(); }
            }
        }
    }

    private static int ReservePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException("FAIL: " + message); }
    }

    private sealed class SecondaryTestDatabase : RavenDatabaseSingleton<SecondaryTestDatabase> {
        protected override string DatabaseName => "TowerStartupConcurrencySecondary";
        protected override string Url => string.Empty;
        protected override string CertificatePath => string.Empty;
    }
}
