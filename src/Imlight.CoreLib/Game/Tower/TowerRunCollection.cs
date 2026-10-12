using System;
using System.Collections.Concurrent;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Newtonsoft.Json.Linq;

namespace Imlight.CoreLib.Game.Tower;

/// <summary>Stores one authoritative document per Adventure run with an epoch and revision fence.</summary>
public sealed class TowerRunCollection {
    public const string CollectionName = "TowerRuns";
    public const int ActiveRosterResetVersion = 1;
    private const int MaxWriteAttempts = 3;

    private readonly IDocumentStore _store;
    private readonly string _processIncarnation;
    private readonly TowerServerOwnership _ownership;
    private readonly ConcurrentDictionary<ulong, string> _wizardDocumentIds = new();

    public TowerRunCollection(IDocumentStore store)
        : this(store, TowerServerOwnership.CurrentProcessOwner) { }

    internal TowerRunCollection(IDocumentStore store, TowerServerOwnership ownership) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ownership = ownership;
        _processIncarnation = ownership?.ProcessIncarnation ?? TowerProcessIncarnation.Current;
    }

    internal TowerRunCollection(IDocumentStore store, string processIncarnation) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (string.IsNullOrWhiteSpace(processIncarnation)) {
            throw new ArgumentException("A process incarnation is required.", nameof(processIncarnation));
        }
        _processIncarnation = processIncarnation;
        _ownership = null;
    }

    public TowerRunState CreateRun(string runId, ulong ownerWizardId, ulong runSeed) {
        var state = TowerRunState.Create(runId, ownerWizardId, runSeed, _processIncarnation);
        using var session = OpenMutationSession();

        var id = DocumentId(runId);
        if (session.Load<TowerRunState>(id) is not null) {
            throw new InvalidOperationException($"Tower run '{runId}' already exists.");
        }
        if (!TouchOwner(session)) { throw new InvalidOperationException("This server process no longer owns Tower writes."); }

        state.ProgressionResetVersion = session
            .Load<TowerPlayerProgression>(TowerAwardCollection.ProgressionId(ownerWizardId))?
            .ProgressionResetVersion ?? 0;

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
        var currentResetVersion = session.Load<TowerPlayerProgression>(
            TowerAwardCollection.ProgressionId(wizardId))?.ProgressionResetVersion ?? 0;
        var runs = session.Advanced.RawQuery<TowerRunState>(
                "from TowerRuns where PendingAwards[].WizardId = $wizardId")
            .AddParameter("wizardId", wizardId)
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .ToList();

        return runs.Where(state => state.ProgressionResetVersion >= currentResetVersion)
            .SelectMany(state => state.PendingAwards ?? [])
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
    /// Applies the one-time curated-roster migration for a wizard. The prior run is fenced and
    /// abandoned, first-clear state and tickets are reset, and a fresh Floor 1 run is created in
    /// the same transaction. Native level, spells, items, and item upgrades are left untouched.
    /// </summary>
    public TowerRunState ResetProgressForNewRoster(TowerRunState expectedRun, ulong runSeed) {
        if (expectedRun is null || expectedRun.OwnerWizardId == 0
            || expectedRun.Status != TowerRunStatus.Active
            || expectedRun.EncounterLifecycle != TowerEncounterLifecycle.NotStarted
            || expectedRun.CurrentManifest is not null
            || expectedRun.ProgressionResetVersion >= ActiveRosterResetVersion) {
            return null;
        }

        var wizardId = expectedRun.OwnerWizardId;
        var wizardDocumentId = FindWizardDocumentId(wizardId);
        if (wizardDocumentId is null) { return null; }

        var newRunId = Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenMutationSession();
                if (_ownership is null) {
                    session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
                }

                var progressionId = TowerAwardCollection.ProgressionId(wizardId);
                var progression = session.Load<TowerPlayerProgression>(progressionId);
                if (progression?.ProgressionResetVersion >= ActiveRosterResetVersion) {
                    if (string.IsNullOrWhiteSpace(progression.ProgressionResetRunId)) { return null; }
                    return session.Load<TowerRunState>(DocumentId(progression.ProgressionResetRunId))?.Copy();
                }

                var stored = session.Load<TowerRunState>(DocumentId(expectedRun.RunId));
                if (stored is null || stored.OwnerWizardId != wizardId
                    || stored.Status != TowerRunStatus.Active
                    || stored.EncounterLifecycle != TowerEncounterLifecycle.NotStarted
                    || stored.CurrentManifest is not null
                    || stored.AuthorityEpoch != expectedRun.AuthorityEpoch
                    || stored.AuthorityLeaseId != expectedRun.AuthorityLeaseId
                    || stored.Revision != expectedRun.Revision) {
                    return null;
                }

                var wizard = session.Load<JObject>(wizardDocumentId);
                if (wizard is null || wizard["CharId"]?.Value<ulong>() != wizardId
                    || wizard["GameStats"] is not JObject gameStats) {
                    return null;
                }
                if (!TouchOwner(session)) { return null; }

                var now = DateTime.UtcNow;
                var createProgression = progression is null;
                progression ??= new TowerPlayerProgression { WizardId = wizardId };
                progression.HighestFloorCleared = 0;
                progression.ClearedFloors = [];
                progression.Tickets = 0;
                progression.ProgressionResetVersion = ActiveRosterResetVersion;
                progression.ProgressionResetRunId = newRunId;
                progression.UpdatedUtc = now;
                gameStats["m_currentEventCurrency1"] = 0;

                stored.Status = TowerRunStatus.Abandoned;
                stored.AuthorityEpoch = checked(stored.AuthorityEpoch + 1);
                stored.AuthorityLeaseId = Guid.NewGuid().ToString("N");
                stored.Revision = checked(stored.Revision + 1);
                stored.EncounterLifecycle = TowerEncounterLifecycle.NotStarted;
                stored.PendingFloorAdvance = false;
                stored.CurrentManifest = null;
                stored.AdmittedPlayerIds = [];
                stored.AdmittedEnemySlots = [];
                stored.FloorEntryResources = [];
                stored.UpdatedUtc = now;

                var nextRun = TowerRunState.Create(newRunId, wizardId, runSeed, _processIncarnation);
                nextRun.ProgressionResetVersion = ActiveRosterResetVersion;
                nextRun.CreatedUtc = now;
                nextRun.UpdatedUtc = now;
                session.Store(nextRun, DocumentId(newRunId));
                session.Advanced.GetMetadataFor(nextRun)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
                if (createProgression) {
                    session.Store(progression, progressionId);
                    session.Advanced.GetMetadataFor(progression)[Raven.Client.Constants.Documents.Metadata.Collection] = "TowerProgressions";
                }

                session.SaveChanges();
                return nextRun;
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) {
                // Reload the run, progression marker, wizard mirror, and reset-run link.
            }
            catch (ConcurrencyException) {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Resets floor access at a safe sanctuary boundary, retaining first-clear history,
    /// all award receipts and every currency balance. Pending rewards must settle first.
    /// </summary>
    public TowerRunState TryResetFloors(TowerRunState expectedRun) {
        if (expectedRun is null || expectedRun.Status != TowerRunStatus.Active
            || expectedRun.EncounterLifecycle != TowerEncounterLifecycle.NotStarted) { return null; }
        try {
            using var session = OpenMutationSession();
            if (_ownership is null) {
                session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            }
            var stored = session.Load<TowerRunState>(DocumentId(expectedRun.RunId));
            if (stored is null || stored.OwnerWizardId != expectedRun.OwnerWizardId
                || stored.Status != TowerRunStatus.Active
                || stored.EncounterLifecycle != TowerEncounterLifecycle.NotStarted
                || stored.AuthorityEpoch != expectedRun.AuthorityEpoch
                || stored.AuthorityLeaseId != expectedRun.AuthorityLeaseId
                || stored.AuthorityProcessIncarnation != _processIncarnation
                || stored.Revision != expectedRun.Revision) { return null; }

            var awardIds = (stored.PendingAwards ?? []).Select(award =>
                TowerAwardCollection.ReceiptId(award.WizardId, award.AwardId)).Distinct().ToArray();
            if (awardIds.Length > 0 && session.Load<TowerAwardReceipt>(awardIds).Values.Any(receipt => receipt is null)) {
                return null;
            }
            if (!TouchOwner(session)) { return null; }
            var progression = session.Load<TowerPlayerProgression>(
                TowerAwardCollection.ProgressionId(stored.OwnerWizardId));
            if (progression is not null) {
                progression.HighestFloorCleared = 0;
                progression.UpdatedUtc = DateTime.UtcNow;
            }

            var reset = TowerRunState.Create(stored.RunId, stored.OwnerWizardId,
                stored.RunSeed, _processIncarnation);
            reset.CreatedUtc = stored.CreatedUtc;
            reset.AuthorityEpoch = checked(stored.AuthorityEpoch + 1);
            reset.AuthorityLeaseId = Guid.NewGuid().ToString("N");
            reset.Revision = checked(stored.Revision + 1);
            reset.AttemptGeneration = checked(stored.AttemptGeneration + 1);
            reset.ProgressionResetVersion = stored.ProgressionResetVersion;
            reset.ChallengeMode = stored.ChallengeMode;
            reset.ChallengeReferenceLevel = stored.ChallengeReferenceLevel;
            reset.PendingAwards = stored.PendingAwards ?? [];
            CopyState(reset, stored);
            stored.Revision = reset.Revision;
            session.SaveChanges();
            return stored.Copy();
        }
        catch (ConcurrencyException) { return null; }
    }

    /// <summary>
    /// Persists a mutation only when its state revision and authority epoch still match the
    /// last committed copy. Conflicts return false so callers must reload and revalidate input.
    /// </summary>
    public bool TrySave(TowerRunState state, long expectedAuthorityEpoch) {
        ArgumentNullException.ThrowIfNull(state);
        if (state.AuthorityEpoch != expectedAuthorityEpoch) { return false; }

        try {
            using var session = OpenMutationSession();
            var stored = session.Load<TowerRunState>(DocumentId(state.RunId));
            if (stored is null
                || stored.OwnerWizardId != state.OwnerWizardId
                || stored.AuthorityEpoch != expectedAuthorityEpoch
                || stored.AuthorityProcessIncarnation != state.AuthorityProcessIncarnation
                || stored.AuthorityLeaseId != state.AuthorityLeaseId
                || stored.Revision != state.Revision) {
                return false;
            }
            if (!TouchOwner(session)) { return false; }

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
                using var session = OpenMutationSession();
                var state = session.Load<TowerRunState>(DocumentId(runId));
                if (state is null || state.OwnerWizardId != ownerWizardId) { return null; }
                if (state.Status is TowerRunStatus.Completed or TowerRunStatus.Abandoned) { return null; }
                if (!TouchOwner(session)) { return null; }

                if (!state.AcquireAuthority(_processIncarnation)) { return null; }
                session.SaveChanges();
                return state;
            }
            catch (ConcurrencyException) when (attempt + 1 < MaxWriteAttempts) {
                // Read the winner's newer token, then acquire the next epoch.
            }
            catch (ConcurrencyException) {
                return null;
            }
        }

        return null;
    }

    /// <summary>Fences the current room and records a voluntary exit for the next checkpoint restart.</summary>
    public bool TryExtractRun(ulong ownerWizardId) {
        var documentId = FindLatestRunId(ownerWizardId);
        if (documentId is null) { return false; }

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++) {
            try {
                using var session = OpenMutationSession();
                var state = session.Load<TowerRunState>(documentId);
                if (state is null || state.OwnerWizardId != ownerWizardId) { return false; }
                if (state.Status != TowerRunStatus.Active) { return true; }
                if (!TouchOwner(session)) { return false; }

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

    private string FindLatestRunId(ulong ownerWizardId) {
        using var session = _store.OpenSession();
        return session.Advanced.RawQuery<RunDocumentReference>(
                "from TowerRuns where OwnerWizardId = $wizardId order by CreatedUtc desc select id() as DocumentId")
            .AddParameter("wizardId", ownerWizardId)
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .ToList()
            .Select(reference => reference.DocumentId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Updates a live Tower combat resource and its run revision in one fenced cluster-wide
    /// transaction. Recovery restoration is also allowed while lifecycle is RecoveryPending.
    /// </summary>
    public bool TryUpdateCombatResources(TowerRunState candidate, string encounterId,
                                         ulong wizardId, int health, int mana) {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(encounterId) || wizardId == 0 || health < 0 || mana < 0) { return false; }

        var wizardDocumentId = FindWizardDocumentId(wizardId);
        if (wizardDocumentId is null) { return false; }

        try {
            using var session = OpenMutationSession();
            var stored = session.Load<TowerRunState>(DocumentId(candidate.RunId));
            var wizard = session.Load<JObject>(wizardDocumentId);
            if (stored is null || wizard is null
                || stored.OwnerWizardId != candidate.OwnerWizardId
                || stored.AuthorityEpoch != candidate.AuthorityEpoch
                || stored.AuthorityProcessIncarnation != candidate.AuthorityProcessIncarnation
                || stored.AuthorityLeaseId != candidate.AuthorityLeaseId
                || stored.Revision != candidate.Revision
                || stored.EncounterLifecycle != candidate.EncounterLifecycle
                || stored.EncounterLifecycle is not (TowerEncounterLifecycle.InCombat or TowerEncounterLifecycle.RecoveryPending)
                || stored.CurrentManifest?.EncounterId != encounterId
                || !(stored.AdmittedPlayerIds ?? []).Contains(wizardId)
                    && stored.EncounterLifecycle == TowerEncounterLifecycle.InCombat) {
                return false;
            }

            if (stored.EncounterLifecycle == TowerEncounterLifecycle.RecoveryPending) {
                var pendingSnapshot = (stored.FloorEntryResources ?? []).FirstOrDefault(snapshot =>
                    snapshot.WizardId == wizardId && snapshot.Floor == stored.CurrentFloor
                    && snapshot.AttemptGeneration == stored.AttemptGeneration && !snapshot.WasRestored);
                if (pendingSnapshot is null) { return false; }
            }

            if (!TouchOwner(session)) { return false; }
            if (wizard["GameStats"] is not JObject gameStats) { return false; }
            gameStats["m_currentHitpoints"] = health;
            gameStats["m_currentMana"] = mana;
            CopyState(candidate, stored);
            stored.Revision = checked(candidate.Revision + 1);
            session.SaveChanges();
            candidate.Revision = stored.Revision;
            return true;
        }
        catch (ConcurrencyException) {
            return false;
        }
    }

    private string FindWizardDocumentId(ulong wizardId) {
        if (_wizardDocumentIds.TryGetValue(wizardId, out var cached)) { return cached; }

        using var session = _store.OpenSession();
        var reference = session.Advanced.RawQuery<RunDocumentReference>(
                "from Wizards where CharId = $wizardId select id() as DocumentId")
            .AddParameter("wizardId", wizardId)
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .SingleOrDefault();
        if (string.IsNullOrWhiteSpace(reference?.DocumentId)) { return null; }
        return _wizardDocumentIds.GetOrAdd(wizardId, reference.DocumentId);
    }

    private IDocumentSession OpenMutationSession() {
        if (_ownership is null) { return _store.OpenSession(); }
        return _store.OpenSession(new SessionOptions {
            TransactionMode = TransactionMode.ClusterWide,
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.None,
        });
    }

    private bool TouchOwner(IDocumentSession session) => _ownership is null || _ownership.Touch(session);

    private sealed class RunDocumentReference { public string DocumentId { get; set; } }

    private static void CopyState(TowerRunState source, TowerRunState target) {
        target.OwnerWizardId = source.OwnerWizardId;
        target.RunSeed = source.RunSeed;
        target.CurrentFloor = source.CurrentFloor;
        target.HighestFloorCleared = source.HighestFloorCleared;
        target.CheckpointFloor = source.CheckpointFloor;
        target.ChallengeMode = source.ChallengeMode;
        target.ChallengeReferenceLevel = source.ChallengeReferenceLevel;
        target.AttemptGeneration = source.AttemptGeneration;
        target.EncounterRecoveryGeneration = source.EncounterRecoveryGeneration;
        target.RecoveryAllowanceRemaining = source.RecoveryAllowanceRemaining;
        target.RecoveryResourcesPending = source.RecoveryResourcesPending;
        target.PendingFloorAdvance = source.PendingFloorAdvance;
        target.LastClearedEncounterId = source.LastClearedEncounterId;
        target.ProgressionResetVersion = source.ProgressionResetVersion;
        target.AuthorityEpoch = source.AuthorityEpoch;
        target.AuthorityProcessIncarnation = source.AuthorityProcessIncarnation;
        target.AuthorityLeaseId = source.AuthorityLeaseId;
        target.LastTakeoverProof = source.LastTakeoverProof?.Copy();
        target.Status = source.Status;
        target.EncounterLifecycle = source.EncounterLifecycle;
        target.AdmittedPlayerIds = [.. (source.AdmittedPlayerIds ?? [])];
        target.AdmittedEnemySlots = [.. (source.AdmittedEnemySlots ?? [])];
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
