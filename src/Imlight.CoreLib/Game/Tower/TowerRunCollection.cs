using System;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Stores one authoritative document per Adventure run with an epoch and revision fence.</summary>
public sealed class TowerRunCollection {
    public const string CollectionName = "TowerRuns";
    private const int MaxWriteAttempts = 3;

    private readonly IDocumentStore _store;
    private readonly string _processIncarnation;

    public TowerRunCollection(IDocumentStore store)
        : this(store, TowerProcessIncarnation.Current) { }

    internal TowerRunCollection(IDocumentStore store, string processIncarnation) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (string.IsNullOrWhiteSpace(processIncarnation)) {
            throw new ArgumentException("A process incarnation is required.", nameof(processIncarnation));
        }
        _processIncarnation = processIncarnation;
    }

    public TowerRunState CreateRun(string runId, ulong ownerWizardId, ulong runSeed) {
        var state = TowerRunState.Create(runId, ownerWizardId, runSeed, _processIncarnation);
        using var session = _store.OpenSession();
        session.Advanced.UseOptimisticConcurrency = true;

        var id = DocumentId(runId);
        if (session.Load<TowerRunState>(id) is not null) {
            throw new InvalidOperationException($"Tower run '{runId}' already exists.");
        }

        session.Store(state, id);
        session.Advanced.GetMetadataFor(state)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
        session.SaveChanges();
        return state;
    }

    public TowerRunState Load(string runId) {
        using var session = _store.OpenSession();
        return session.Load<TowerRunState>(DocumentId(runId));
    }

    /// <summary>Returns unreceipted awards for a wizard across active and completed runs.</summary>
    public TowerPendingAward[] LoadPendingAwards(ulong wizardId) {
        using var session = _store.OpenSession();
        var runs = session.Advanced.RawQuery<TowerRunState>(
                "from TowerRuns where PendingAwards[].WizardId = $wizardId")
            .AddParameter("wizardId", wizardId)
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .ToList();

        return runs.SelectMany(state => state.PendingAwards ?? [])
            .Where(award => award.WizardId == wizardId
                && !session.Advanced.Exists(TowerAwardCollection.ReceiptId(wizardId, award.AwardId)))
            .GroupBy(award => award.AwardId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public TowerRunState CreateOrResume(ulong ownerWizardId, ulong runSeed) {
        using (var session = _store.OpenSession()) {
            var existing = session.Advanced.RawQuery<TowerRunState>(
                    "from TowerRuns where OwnerWizardId = $wizardId")
                .AddParameter("wizardId", ownerWizardId)
                .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
                .ToList()
                .OrderByDescending(state => state.CreatedUtc)
                .FirstOrDefault();

            if (existing is not null && existing.Status is not (TowerRunStatus.Completed or TowerRunStatus.Abandoned)) {
                if (existing.Status == TowerRunStatus.Active
                    && string.Equals(existing.AuthorityProcessIncarnation, _processIncarnation, StringComparison.Ordinal)) {
                    return existing;
                }

                var acquired = AcquireAuthority(existing.RunId, ownerWizardId);
                if (acquired is null) { return null; }
                if (acquired.Status is TowerRunStatus.Defeated or TowerRunStatus.Extracted) {
                    acquired.RestartFromCheckpoint(authorityAlreadyReacquired: true);
                    if (!TrySave(acquired, acquired.AuthorityEpoch)) { return null; }
                }
                return acquired;
            }
        }

        var runId = Guid.NewGuid().ToString("N");
        return CreateRun(runId, ownerWizardId, runSeed);
    }

    /// <summary>
    /// Persists a mutation only when its state revision and authority epoch still match the
    /// last committed copy. Conflicts return false so callers must reload and revalidate input.
    /// </summary>
    public bool TrySave(TowerRunState state, long expectedAuthorityEpoch) {
        ArgumentNullException.ThrowIfNull(state);
        if (state.AuthorityEpoch != expectedAuthorityEpoch) { return false; }

        try {
            using var session = _store.OpenSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var stored = session.Load<TowerRunState>(DocumentId(state.RunId));
            if (stored is null
                || stored.OwnerWizardId != state.OwnerWizardId
                || stored.AuthorityEpoch != expectedAuthorityEpoch
                || stored.AuthorityProcessIncarnation != state.AuthorityProcessIncarnation
                || stored.AuthorityLeaseId != state.AuthorityLeaseId
                || stored.Revision != state.Revision) {
                return false;
            }

            CopyState(state, stored);
            stored.Revision = checked(state.Revision + 1);
            session.SaveChanges();
            state.Revision = stored.Revision;
            return true;
        }
        catch (ConcurrencyException) {
            return false;
        }
    }

    /// <summary>Acquires a recovered run and fences every previously loaded actor snapshot.</summary>
    public TowerRunState AcquireAuthority(string runId, ulong ownerWizardId) {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = _store.OpenSession();
                session.Advanced.UseOptimisticConcurrency = true;
                var state = session.Load<TowerRunState>(DocumentId(runId));
                if (state is null || state.OwnerWizardId != ownerWizardId) { return null; }
                if (state.Status is TowerRunStatus.Completed or TowerRunStatus.Abandoned) { return null; }

                if (!state.AcquireAuthority(_processIncarnation)) { return null; }
                session.SaveChanges();
                return state;
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) {
                // Read the winner's newer token, then acquire the next epoch.
            }
        }

        return null;
    }

    /// <summary>Fences the current room and records a voluntary exit for the next checkpoint restart.</summary>
    public bool TryExtractRun(ulong ownerWizardId) {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = _store.OpenSession();
                session.Advanced.UseOptimisticConcurrency = true;
                var state = session.Advanced.RawQuery<TowerRunState>(
                        "from TowerRuns where OwnerWizardId = $wizardId")
                    .AddParameter("wizardId", ownerWizardId)
                    .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
                    .ToList()
                    .OrderByDescending(run => run.CreatedUtc)
                    .FirstOrDefault();
                if (state is null) { return false; }
                if (state.Status != TowerRunStatus.Active) { return true; }

                state.Status = TowerRunStatus.Extracted;
                state.AuthorityEpoch = checked(state.AuthorityEpoch + 1);
                state.AuthorityLeaseId = Guid.NewGuid().ToString("N");
                state.Revision = checked(state.Revision + 1);
                state.UpdatedUtc = DateTime.UtcNow;
                session.SaveChanges();
                return true;
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) {
                // Re-read the winner's state before retrying the extraction.
            }
            catch (ConcurrencyException) {
                return false;
            }
        }

        return false;
    }

    private static void CopyState(TowerRunState source, TowerRunState target) {
        target.OwnerWizardId = source.OwnerWizardId;
        target.RunSeed = source.RunSeed;
        target.CurrentFloor = source.CurrentFloor;
        target.HighestFloorCleared = source.HighestFloorCleared;
        target.CheckpointFloor = source.CheckpointFloor;
        target.AttemptGeneration = source.AttemptGeneration;
        target.EncounterRecoveryGeneration = source.EncounterRecoveryGeneration;
        target.RecoveryAllowanceRemaining = source.RecoveryAllowanceRemaining;
        target.RecoveryResourcesPending = source.RecoveryResourcesPending;
        target.AuthorityEpoch = source.AuthorityEpoch;
        target.AuthorityProcessIncarnation = source.AuthorityProcessIncarnation;
        target.AuthorityLeaseId = source.AuthorityLeaseId;
        target.LastTakeoverProof = source.LastTakeoverProof?.Copy();
        target.Status = source.Status;
        target.CurrentManifest = source.Copy().CurrentManifest;
        target.FloorEntryResources = [.. (source.FloorEntryResources ?? []).Select(snapshot => snapshot.Copy())];
        target.PendingAwards = [.. (source.PendingAwards ?? [])];
        target.UpdatedUtc = source.UpdatedUtc;
    }

    private static string DocumentId(string runId) {
        if (string.IsNullOrWhiteSpace(runId)) { throw new ArgumentException("A run id is required.", nameof(runId)); }
        return "tower-runs/" + Uri.EscapeDataString(runId);
    }
}
