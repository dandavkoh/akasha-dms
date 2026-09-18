namespace Akasha.Domain.Tenants;

/// <summary>
/// A tenant is the top-level isolation boundary of the system.
/// Every tenant-scoped entity references a tenant.
/// </summary>
public sealed class Tenant
{
    public const int MaxNameLength = 200;
    public const int MaxSlugLength = 100;

    private Tenant()
    {
    }

    public Tenant(string name, string slug)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required (max {MaxNameLength} chars).", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(slug) || slug.Length > MaxSlugLength)
        {
            throw new ArgumentException($"Slug is required (max {MaxSlugLength} chars).", nameof(slug));
        }

        Id = Guid.CreateVersion7();
        Name = name;
        Slug = slug;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required (max {MaxNameLength} chars).", nameof(name));
        }

        Name = name;
    }
}
