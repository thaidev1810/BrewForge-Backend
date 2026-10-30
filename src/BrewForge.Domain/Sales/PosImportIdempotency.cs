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

    public PosImportIdempotency(long userId, string idempotencyKey, long jobNumber, string? payloadHash,
        DateTimeOffset createdAt)
    {
        UserId = userId;
        IdempotencyKey = idempotencyKey;
        JobNumber = jobNumber;
        PayloadHash = payloadHash;
        CreatedAt = createdAt;
    }

    public long UserId { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>The import job that owns this key; its result lives in the audit log under this number.</summary>
    public long JobNumber { get; private set; }

    /// <summary>
    /// A fingerprint of the file imported under this key. A retry with the same key must carry the same
    /// file to replay; a different file under a reused key is refused. Null for rows made before this was added.
    /// </summary>
    public string? PayloadHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
