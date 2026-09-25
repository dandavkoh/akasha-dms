using System.ComponentModel.DataAnnotations;

namespace Akasha.Infrastructure.Jobs;

public sealed class BackgroundJobOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>Idle delay between queue polls.</summary>
    [Range(typeof(TimeSpan), "00:00:00.1", "1.00:00:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a running job's cancellation flag is checked.</summary>
    [Range(typeof(TimeSpan), "00:00:00.05", "1.00:00:00")]
    public TimeSpan CancellationCheckInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Max jobs claimed per poll cycle.</summary>
    [Range(1, 100)]
    public int BatchSize { get; set; } = 10;

    /// <summary>Default attempts (including the first) before a job is dead.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 5;

    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}
