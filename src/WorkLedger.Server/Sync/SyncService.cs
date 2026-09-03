using WorkLedger.Contracts.Sync;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Sync;

/// <summary>
/// Push/pull reconciliation. Every rule below is mirrored by the client and by
/// plans/S3-sync-endpoints.md — if one changes, all three change together, because a merge rule the
/// two sides disagree about is how devices silently diverge.
///
/// <para><b>A. Higher version wins.</b> An incoming change is applied when
/// <c>incoming.Version &gt; existing.Version</c>.</para>
///
/// <para><b>B. Equal or lower loses</b>, and the server answers with its own version + payload so
/// the pusher can adopt the winner immediately. There is deliberately <b>no timestamp tie-break</b>:
/// the client's rule is a bare <c>remoteVersion &gt; (localVersion ?? 0)</c> and its RemoteChange
/// type has no CreatedAt field at all, so a timestamp rule here could not be mirrored there. We
/// carry SyncChangeDto.CreatedAt and store it, but it never decides anything.</para>
///
/// <para><b>C. No existing row → insert</b>, taking Version/OriginDeviceId from the change and the
/// data columns from the payload.</para>
///
/// <para><b>D. Deletes tombstone, and the server bumps the version.</b> A delete is accepted at
/// <c>&gt;=</c>, not <c>&gt;</c>, and is stored at <c>existing.Version + 1</c>. Both halves are
/// forced by the client (see WL-93): its Delete*Async queues the delete without bumping the version,
/// so a strict <c>&gt;</c> would reject every delete ever pushed; and the receiving client applies
/// remote changes with that same strict <c>&gt;</c>, so a tombstone echoed back at the original
/// version would be dropped by the other device. Bumping makes the tombstone strictly beat every
/// peer's copy, so the delete actually lands. This is the one deliberate deviation from strict LWW;
/// it can be reverted to a plain <c>&gt;</c> once WL-93 ships.</para>
///
/// <para><b>E. Replays are accepted no-ops that touch nothing.</b> Not merely "don't bump the
/// version" — <i>no EF write at all</i>. WorkLedgerDbContext stamps UpdatedAt on every modified row,
/// and UpdatedAt is the pull cursor, so an incidental write would re-deliver the row to every other
/// device on their next pull. A no-op write is not free here; it is a broadcast.</para>
///
/// <para><b>F. Results are positional</b> — one SyncItemResult per input change, in request order.
/// EntityId is not a key: one batch legitimately carries create → update → delete for the same
/// entity, and the client's own ack list is keyed by a local SyncQueue.Id the server never sees
/// (WL-94).</para>
/// </summary>
public sealed class SyncService(WorkLedgerDbContext db)
{
    public async Task<SyncPushResponse> PushAsync(
        SyncPushRequest request, int userId, string callerDeviceId, CancellationToken cancellationToken)
    {
        var changes = request.Changes;
        var results = new SyncItemResult[changes.Count];

        // The batch is applied parents-first (rule: projects → tasks → time entries) but answered in
        // request order (rule F), which is why the result array is indexed rather than appended to.
        // OrderBy is a stable sort, so several changes for one entity keep their original relative
        // order — reordering those would invert the outcome (a create → delete pair would resurrect
        // the row instead of tombstoning it).
        var ordered = changes
            .Select((change, index) => (Change: change, Index: index))
            .OrderBy(item => RankOf(item.Change.EntityType));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var (change, index) in ordered)
        {
            results[index] = await ApplyAsync(change, userId, cancellationToken);

            // Saved per item, inside the one transaction: a later change in the same batch has to be
            // able to see an earlier one (create then update of the same entity), and EF's change
            // tracker does not surface unsaved inserts to a subsequent query. The transaction still
            // makes the batch all-or-nothing.
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SyncPushResponse(results, DateTime.UtcNow);
    }

    public async Task<SyncPullResponse> PullAsync(
        DateTime since, int userId, string callerDeviceId, CancellationToken cancellationToken)
    {
        var changes = new List<SyncChangeDto>();

        // Handlers are walked in dependency order so parents arrive before their children, which is
        // what a receiving client needs to resolve parent SyncIds as it merges.
        foreach (var handler in SyncEntityHandlers.ByEntityType.Values.OrderBy(h => h.OrderRank))
        {
            var rows = await handler.QueryChangedAsync(db, userId, since, callerDeviceId, cancellationToken);
            changes.AddRange(rows.Select(row => ToChangeDto(handler, row)));
        }

        // ServerTime is what the client should send as its next `since`. Read after the queries, so
        // anything written while this request was running falls after the cursor and is delivered
        // next time rather than being skipped.
        return new SyncPullResponse(changes, DateTime.UtcNow);
    }

    private async Task<SyncItemResult> ApplyAsync(SyncChangeDto change, int userId, CancellationToken cancellationToken)
    {
        // Per-item validation failures fail only their own item (rule: never 500 a whole batch
        // because one row was malformed — the other 99 changes in it are perfectly good).
        if (change.EntityType is null || !SyncEntityHandlers.ByEntityType.TryGetValue(change.EntityType, out var handler))
        {
            return Failed(change, 0, $"Unknown EntityType '{change.EntityType}'.");
        }

        if (!IsKnownOperation(change.Operation))
        {
            return Failed(change, 0, $"Unknown Operation '{change.Operation}'.");
        }

        if (change.EntityId == Guid.Empty)
        {
            return Failed(change, 0, "EntityId (the entity's SyncId) is required.");
        }

        if (change.Version < 1)
        {
            // Client entities are born at Version 1 (LocalDbService.StampForInsert), so a 0 here
            // means the pusher failed to lift Version out of PayloadJson — better to say so than to
            // let a version-0 row lose every comparison it will ever be in.
            return Failed(change, 0, "Version must be at least 1.");
        }

        var isDelete = string.Equals(change.Operation, SyncOperations.Delete, StringComparison.OrdinalIgnoreCase);
        var existing = await handler.FindAsync(db, userId, change.EntityId, cancellationToken);

        if (existing is null)
        {
            return Insert(handler, change, userId, isDelete);
        }

        return isDelete
            ? ApplyDelete(handler, change, existing)
            : ApplyUpsert(handler, change, existing);
    }

    /// <summary>Rule C — and, for a delete of an entity we have never seen, a tombstone insert.</summary>
    private SyncItemResult Insert(ISyncEntityHandler handler, SyncChangeDto change, int userId, bool isDelete)
    {
        var entity = handler.NewRow(userId, change.EntityId);

        // Seeded from the change first so a delete with no usable payload still gets a sane row;
        // the payload overwrites CreatedAt with the entity's own creation time when present.
        entity.CreatedAt = SyncTime.ToUtc(change.CreatedAt);

        var error = handler.ApplyPayload(entity, change.PayloadJson, required: !isDelete);
        if (error is not null)
        {
            return Failed(change, 0, error);
        }

        entity.Version = change.Version;
        entity.OriginDeviceId = change.OriginDeviceId;
        entity.IsDeleted = isDelete;
        handler.Add(db, entity);

        // A tombstone for a row we never held still propagates: another device may have received
        // the create directly from a third device, and this is the only signal it will ever get.
        return Accepted(change, entity.Version);
    }

    /// <summary>Rule D.</summary>
    private static SyncItemResult ApplyDelete(ISyncEntityHandler handler, SyncChangeDto change, IVersionedSyncEntity existing)
    {
        if (existing.IsDeleted && change.Version <= existing.Version)
        {
            // Already tombstoned at or above this version. Accepting without writing is what stops a
            // retried batch from ratcheting the version upward on every replay (rule E).
            return Accepted(change, existing.Version);
        }

        if (change.Version < existing.Version)
        {
            return ServerWins(handler, change, existing);
        }

        // The client sends the entity as the payload of a delete, and we only get here when that
        // payload is at least as new as the row, so applying it keeps the tombstone's columns at the
        // last state anyone saw. Best-effort: a missing or unusable payload still tombstones.
        handler.ApplyPayload(existing, change.PayloadJson, required: false);
        existing.IsDeleted = true;
        existing.OriginDeviceId = change.OriginDeviceId;
        existing.Version = existing.Version + 1;

        return Accepted(change, existing.Version);
    }

    /// <summary>Rules A, B and E.</summary>
    private static SyncItemResult ApplyUpsert(ISyncEntityHandler handler, SyncChangeDto change, IVersionedSyncEntity existing)
    {
        if (change.Version > existing.Version)
        {
            var error = handler.ApplyPayload(existing, change.PayloadJson, required: true);
            if (error is not null)
            {
                return Failed(change, existing.Version, error);
            }

            existing.Version = change.Version;
            existing.OriginDeviceId = change.OriginDeviceId;

            // A winning update revives a tombstone: LWW applied consistently means the newest write
            // wins whatever it says, and "edited after someone else deleted it" resolves to alive.
            existing.IsDeleted = false;

            return Accepted(change, existing.Version);
        }

        if (IsSameDevice(existing.OriginDeviceId, change.OriginDeviceId))
        {
            // The server's row is this device's own write at the same or a newer version, so the
            // change is already reflected — a replay, a self-echo, or an ack the client never
            // received. Accepting it (without writing) lets the client retire the queue row; calling
            // it a conflict would leave the row pending and re-pushed on every subsequent sync,
            // forever.
            return Accepted(change, existing.Version);
        }

        // Genuine conflict: same-or-lower version from a different device. The server keeps its row
        // and hands back the winner. Note the client must retire the queue row on this outcome too
        // (adopting AuthoritativePayloadJson) or it will re-push it indefinitely — that is WL-94's
        // half of this contract.
        return ServerWins(handler, change, existing);
    }

    private static SyncChangeDto ToChangeDto(ISyncEntityHandler handler, IVersionedSyncEntity row) => new(
        handler.EntityType,
        row.SyncId,
        row.IsDeleted ? SyncOperations.Delete : SyncOperations.Update,
        handler.SerializePayload(row),
        row.Version,
        row.OriginDeviceId ?? string.Empty,
        row.CreatedAt);

    private static SyncItemResult Accepted(SyncChangeDto change, int authoritativeVersion)
        => new(change.EntityId, true, authoritativeVersion, null, null);

    /// <summary>
    /// The server's row beat the pushed change, so hand the winner back for immediate adoption.
    /// <para>
    /// <c>AuthoritativeOperation</c> says what the payload <i>is</i>, mirroring what
    /// <see cref="ToChangeDto"/> would emit for the same row. `existing` here can be a tombstone —
    /// device C deletes, device B pushes a stale update, the tombstone wins — and without the flag a
    /// client adopting the payload can only guess "update", which resurrects the entity locally and
    /// permanently. Rule E means this branch writes nothing, so the winner's UpdatedAt stays behind
    /// the client's cursor and no later incremental pull corrects the guess. See WL-98.
    /// </para>
    /// </summary>
    private static SyncItemResult ServerWins(ISyncEntityHandler handler, SyncChangeDto change, IVersionedSyncEntity existing)
        => new(
            change.EntityId,
            false,
            existing.Version,
            handler.SerializePayload(existing),
            "Server version wins.",
            existing.IsDeleted ? SyncOperations.Delete : SyncOperations.Update);

    private static SyncItemResult Failed(SyncChangeDto change, int authoritativeVersion, string reason)
        => new(change.EntityId, false, authoritativeVersion, null, reason);

    private static bool IsSameDevice(string? left, string? right)
        => !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsKnownOperation(string? operation)
        => string.Equals(operation, SyncOperations.Create, StringComparison.OrdinalIgnoreCase)
           || string.Equals(operation, SyncOperations.Update, StringComparison.OrdinalIgnoreCase)
           || string.Equals(operation, SyncOperations.Delete, StringComparison.OrdinalIgnoreCase);

    private static int RankOf(string? entityType)
        => entityType is not null && SyncEntityHandlers.ByEntityType.TryGetValue(entityType, out var handler)
            ? handler.OrderRank
            : int.MaxValue;
}
