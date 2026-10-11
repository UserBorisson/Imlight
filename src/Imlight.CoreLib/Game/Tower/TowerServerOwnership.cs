using System;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>
/// Owns Tower writes for one server process. Every durable Tower mutation touches the
/// compare-exchange lease in the same cluster-wide transaction as its documents.
/// </summary>
internal sealed class TowerServerOwnership {
    public const string LeaseKey = "imlight/tower/server-ownership";
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private static readonly object s_processLock = new();
    private static TowerServerOwnership s_processOwner;
    private static int s_processOwnerReferences;

    private readonly IDocumentStore _store;
    private readonly string _processIncarnation;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _leaseDuration;
    private readonly object _localLock = new();
    private string _leaseId;

    public string ProcessIncarnation => _processIncarnation;
    public string LeaseId => _leaseId ?? string.Empty;
    public bool IsOwned => !string.IsNullOrWhiteSpace(_leaseId);

    internal TowerServerOwnership(IDocumentStore store, string processIncarnation)
        : this(store, processIncarnation, () => DateTime.UtcNow, DefaultLeaseDuration) { }

    internal TowerServerOwnership(IDocumentStore store, string processIncarnation,
                                  Func<DateTime> utcNow, TimeSpan leaseDuration) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _processIncarnation = string.IsNullOrWhiteSpace(processIncarnation)
            ? throw new ArgumentException("A process incarnation is required.", nameof(processIncarnation))
            : processIncarnation;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _leaseDuration = leaseDuration > TimeSpan.Zero
            ? leaseDuration
            : throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    internal static TowerServerOwnership RegisterProcessOwner(IDocumentStore store) {
        lock (s_processLock) {
            if (s_processOwner is null) {
                s_processOwner = new TowerServerOwnership(store, TowerProcessIncarnation.Current);
            }
            else if (!ReferenceEquals(s_processOwner._store, store)) {
                throw new InvalidOperationException("A server process cannot own Tower leases for multiple Raven stores.");
            }

            s_processOwnerReferences++;
            return s_processOwner;
        }
    }

    internal static TowerServerOwnership CurrentProcessOwner {
        get { lock (s_processLock) { return s_processOwner; } }
    }

    internal static void UnregisterProcessOwner(TowerServerOwnership owner) {
        lock (s_processLock) {
            if (!ReferenceEquals(owner, s_processOwner) || s_processOwnerReferences == 0) { return; }
            s_processOwnerReferences--;
            if (s_processOwnerReferences > 0) { return; }

            owner.Release();
            s_processOwner = null;
        }
    }

    /// <summary>Acquires, renews, or reacquires the process lease after a prior expiry.</summary>
    public bool TryAcquire() {
        lock (_localLock) {
            for (var attempt = 0; attempt < 3; attempt++) {
                try {
                    using var session = OpenClusterSession();
                    var existing = session.Advanced.ClusterTransaction
                        .GetCompareExchangeValue<TowerServerLeaseRecord>(LeaseKey);
                    var now = _utcNow();
                    var current = existing?.Value;
                    if (current is not null
                        && current.ExpiresUtc > now
                        && !string.Equals(current.OwnerProcessIncarnation, _processIncarnation, StringComparison.Ordinal)) {
                        _leaseId = null;
                        return false;
                    }

                    var leaseId = current is not null
                        && string.Equals(current.OwnerProcessIncarnation, _processIncarnation, StringComparison.Ordinal)
                        ? current.LeaseId
                        : Guid.NewGuid().ToString("N");
                    var next = new TowerServerLeaseRecord {
                        OwnerProcessIncarnation = _processIncarnation,
                        LeaseId = leaseId,
                        ExpiresUtc = now.Add(_leaseDuration),
                        TouchToken = Guid.NewGuid().ToString("N"),
                    };
                    if (existing is null) {
                        session.Advanced.ClusterTransaction.CreateCompareExchangeValue(LeaseKey, next);
                    }
                    else {
                        existing.Value = next;
                    }

                    session.SaveChanges();
                    _leaseId = leaseId;
                    return true;
                }
                catch (ClusterTransactionConcurrencyException) {
                    // A different process or heartbeat won the lease compare-exchange.
                    if (attempt == 2) { return false; }
                }
            }

            return false;
        }
    }

    public bool Renew() => TryAcquire();

    /// <summary>
    /// Adds the current process lease to a caller's cluster-wide transaction. Updating the
    /// compare-exchange value makes its index a commit-time fence against takeover races.
    /// </summary>
    public bool Touch(IDocumentSession session) {
        ArgumentNullException.ThrowIfNull(session);
        lock (_localLock) {
            if (string.IsNullOrWhiteSpace(_leaseId)) { return false; }

            var current = session.Advanced.ClusterTransaction
                .GetCompareExchangeValue<TowerServerLeaseRecord>(LeaseKey);
            if (current?.Value is not { } lease
                || lease.ExpiresUtc <= _utcNow()
                || !string.Equals(lease.OwnerProcessIncarnation, _processIncarnation, StringComparison.Ordinal)
                || !string.Equals(lease.LeaseId, _leaseId, StringComparison.Ordinal)) {
                _leaseId = null;
                return false;
            }

            current.Value = new TowerServerLeaseRecord {
                OwnerProcessIncarnation = _processIncarnation,
                LeaseId = _leaseId,
                ExpiresUtc = _utcNow().Add(_leaseDuration),
                // Ensure every transaction changes the compare-exchange value even when
                // two writes happen within the same clock tick.
                TouchToken = Guid.NewGuid().ToString("N"),
            };
            return true;
        }
    }

    public void Release() {
        lock (_localLock) {
            if (string.IsNullOrWhiteSpace(_leaseId)) { return; }

            try {
                using var session = OpenClusterSession();
                var current = session.Advanced.ClusterTransaction
                    .GetCompareExchangeValue<TowerServerLeaseRecord>(LeaseKey);
                if (current?.Value is { } lease
                    && string.Equals(lease.OwnerProcessIncarnation, _processIncarnation, StringComparison.Ordinal)
                    && string.Equals(lease.LeaseId, _leaseId, StringComparison.Ordinal)) {
                    session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(current);
                    session.SaveChanges();
                }
            }
            catch (ClusterTransactionConcurrencyException) {
                // A newer process owns the key; its lease must remain untouched.
            }

            _leaseId = null;
        }
    }

    private IDocumentSession OpenClusterSession() {
        // The application store defaults document optimistic concurrency on. RavenDB
        // cluster-wide transactions reject that mode and use compare-exchange indexes.
        return _store.OpenSession(new SessionOptions {
            TransactionMode = TransactionMode.ClusterWide,
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.None,
        });
    }
}

internal sealed class TowerServerLeaseRecord {
    public string OwnerProcessIncarnation { get; set; } = string.Empty;
    public string LeaseId { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
    public string TouchToken { get; set; } = string.Empty;
}

internal sealed class TowerOwnershipHeartbeatMessage { }
