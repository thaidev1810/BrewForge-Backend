using BrewForge.Domain.Common;

namespace BrewForge.Domain.Training;

/// <summary>The <c>certificate_status</c> enumeration.</summary>
public enum CertificateStatus
{
    Valid,
    NeedsRecert,
    Superseded,
}

/// <summary>
/// The completion certificate, bound to the recipe version the course was
/// built from, never to a recipe in general (BR-13). There is no public way
/// to construct one: a certificate is a consequence of passing, issued by the
/// domain, not a thing a user creates.
/// </summary>
public sealed class Certificate : INeverDeleted
{
    private Certificate() { }

    internal Certificate(long userId, long courseId, long? recipeVersionId, DateTimeOffset issuedAt)
    {
        UserId = userId;
        CourseId = courseId;
        RecipeVersionId = recipeVersionId;
        IssuedAt = issuedAt;
    }

    public long Id { get; private set; }
    public long UserId { get; private set; }
    public long CourseId { get; private set; }
    public long? RecipeVersionId { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public CertificateStatus Status { get; private set; } = CertificateStatus.Valid;
    public long? SupersededBy { get; private set; }

    public bool IsValid => Status == CertificateStatus.Valid;

    // BR-15: flagged as requiring re-training, never deleted.
    string INeverDeleted.RetentionRule => "BR-15";

    /// <summary>Whether this certificate qualifies its holder on the given recipe version.</summary>
    public bool Certifies(long userId, long recipeVersionId) =>
        IsValid && UserId == userId && RecipeVersionId == recipeVersionId;

    /// <summary>The bound version was superseded: the holder needs re-training (BR-15).</summary>
    public void FlagForRecertification()
    {
        if (Status == CertificateStatus.Valid) Status = CertificateStatus.NeedsRecert;
    }

    /// <summary>The certificate that replaces this one, kept as an object until both have ids.</summary>
    public Certificate? Successor { get; private set; }

    /// <summary>The holder earned a newer certificate that replaces this one. This one is kept (BR-15).</summary>
    internal void SupersedeBy(Certificate newer)
    {
        Status = CertificateStatus.Superseded;
        Successor = newer;
    }

    /// <summary>The holder passed again on the same version: the certificate is valid again as of now.</summary>
    internal void Renew(DateTimeOffset now)
    {
        Status = CertificateStatus.Valid;
        IssuedAt = now;
        Successor = null;
        SupersededBy = null;
    }
}
