namespace WorkLedger.Contracts.Sync;

/// <summary>
/// Wire values for <see cref="SyncChangeDto.EntityType"/>. These are NOT free to rename: they are
/// fixed by the client's LocalDbService.EntityTypes, which already writes them into its SyncQueue
/// rows (and into files written by its FileSyncProvider). Changing one here silently stops matching
/// changes the client has already queued.
/// </summary>
public static class SyncEntityTypes
{
    public const string Project = "project";
    public const string Task = "task";
    public const string TimeEntry = "timeentry";
}

/// <summary>
/// Wire values for <see cref="SyncChangeDto.Operation"/>, fixed by the client's
/// LocalDbService.SyncOperations. Note the server treats Create and Update identically — what
/// decides insert-vs-update is whether a row already exists for (UserId, SyncId), not the label
/// the client attached, because the two sides can disagree about which device saw the entity first.
/// </summary>
public static class SyncOperations
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
}
