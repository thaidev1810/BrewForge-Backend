namespace BrewForge.Domain.Sales;

/// <summary>
/// The durable claim that a POS import under a given Idempotency-Key was already made by a user,
/// pointing at the job whose stored result a retry replays. Its primary key is
/// (<see cref="UserId"/>, <see cref="IdempotencyKey"/>): the database thereby lets only one import
/// per key commit, so a retried upload — whether it races the first or comes long after — cannot
/// apply the same file a second time, and is answered with the original result instead.
/// </summary>
public sealed class PosImportIdempotency
{
    private PosImportIdempotency() { }

    public PosImportIdempotency(long userId, string idempotencyKey, long jobNumber, DateTimeOffset createdAt)
    {
        UserId = userId;
        IdempotencyKey = idempotencyKey;
        JobNumber = jobNumber;
        CreatedAt = createdAt;
    }

    public long UserId { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>The import job that owns this key; its result lives in the audit log under this number.</summary>
    public long JobNumber { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
