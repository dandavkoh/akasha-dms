using System.ComponentModel.DataAnnotations;

namespace Akasha.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// PostgreSQL connection string. Must be supplied via environment
    /// (Database__ConnectionString) or a secrets store in production.
    /// Never log this value.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Applies pending EF Core migrations on startup. Migrations are idempotent
    /// and ship with the application, so this also works offline.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;
}
