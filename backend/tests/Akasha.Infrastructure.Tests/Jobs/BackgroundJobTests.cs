using Akasha.Application.Common;
using Akasha.Application.Identity;
using Akasha.Application.Jobs;
using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Jobs;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Jobs;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Akasha.Infrastructure.Tests.Jobs;

/// <summary>
/// Phase 16: background job infrastructure on real PostgreSQL. Real queue, real worker,
/// real claims/retries — only the handler is a test double.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BackgroundJobTests(PostgresFixture postgres)
{
    private readonly BackgroundJobOptions _options = new()
    {
        PollInterval = TimeSpan.FromHours(1), // the host loop is not used; tests drive ProcessAvailableAsync directly
        CancellationCheckInterval = TimeSpan.FromMilliseconds(20),
        BatchSize = 10,
        MaxAttempts = 3,
        InitialRetryDelay = TimeSpan.FromSeconds(30), // long: tests backdate due retries explicitly instead of sleeping
        MaxRetryDelay = TimeSpan.FromMinutes(1),
    };

    private static async Task MakeDueAsync(Harness harness, Guid jobId)
    {
        await using var scope = harness.Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AkashaDbContext>().BackgroundJobs
            .IgnoreQueryFilters()
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.ScheduledAtUtc, DateTimeOffset.UtcNow));
    }

    /// <summary>Shared across scopes: what the handler observed and how it should behave.</summary>
    private sealed class JobRecorder
    {
        public List<BackgroundJobContext> Executed { get; } = [];
        public List<Guid> ScopedTenantIds { get; } = [];
        public List<CurrentUser> ScopedUsers { get; } = [];
        public List<int> VisibleAuditEntries { get; } = [];
        public Func<BackgroundJobContext, AkashaDbContext, CancellationToken, Task>? OnExecute { get; set; }
    }

    private sealed class TestJobHandler(JobRecorder recorder, AkashaDbContext dbContext, ITenantContext tenantContext, ICurrentUser currentUser)
        : IBackgroundJobHandler
    {
        public const string JobType = "test.job";

        public string Type => JobType;

        public async Task ExecuteAsync(BackgroundJobContext context, CancellationToken cancellationToken)
        {
            recorder.Executed.Add(context);
            recorder.ScopedTenantIds.Add(tenantContext.TenantId);
            recorder.ScopedUsers.Add(currentUser.Require());
            recorder.VisibleAuditEntries.Add(await dbContext.AuditEntries.CountAsync(cancellationToken));
            if (recorder.OnExecute is not null)
            {
                await recorder.OnExecute(context, dbContext, cancellationToken);
            }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required BackgroundJobWorker Worker { get; init; }
        public required JobRecorder Recorder { get; init; }
        public required Tenant TenantA { get; init; }
        public required Tenant TenantB { get; init; }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

        /// <summary>Reads a job across tenants with tracking disabled (admin/poller view for assertions).</summary>
        public async Task<BackgroundJob> GetJobAsync(Guid jobId)
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AkashaDbContext>()
                .BackgroundJobs.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(job => job.Id == jobId);
        }

        public async Task<Guid> EnqueueAsync(
            Guid tenantId,
            string payload,
            string? idempotencyKey = null,
            string type = TestJobHandler.JobType,
            CurrentUser? user = null,
            string? correlationId = null,
            DateTimeOffset? notBeforeUtc = null)
        {
            await using var scope = Provider.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            provider.GetRequiredService<ITenantContext>().Establish(tenantId);
            if (user is not null)
            {
                provider.GetRequiredService<ICurrentUser>().Establish(user);
            }

            if (correlationId is not null)
            {
                provider.GetRequiredService<ICorrelationContext>().Establish(correlationId);
            }

            return await provider.GetRequiredService<IBackgroundJobQueue>().EnqueueAsync(
                new EnqueueBackgroundJob(type, payload, idempotencyKey, notBeforeUtc));
        }

        public async Task CancelAsync(Guid tenantId, Guid jobId)
        {
            await using var scope = Provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Establish(tenantId);
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>().CancelAsync(jobId);
        }

        public async Task<int> CountJobsAsync(Guid jobId)
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AkashaDbContext>()
                .BackgroundJobs.IgnoreQueryFilters().CountAsync(job => job.Id == jobId);
        }
    }

    private async Task<Harness> CreateHarnessAsync()
    {
        var recorder = new JobRecorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(_options));
        services.AddDbContext<AkashaDbContext>(options => options.UseAkashaNpgsql(postgres.ConnectionString, 30));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentUser, CurrentUserContext>();
        services.AddScoped<ICorrelationContext, CorrelationContext>();
        services.AddScoped<IBackgroundJobQueue, BackgroundJobQueue>();
        services.AddSingleton(recorder);
        services.AddScoped<TestJobHandler>();
        services.AddScoped<IBackgroundJobHandler>(provider => provider.GetRequiredService<TestJobHandler>());
        var provider = services.BuildServiceProvider();

        var slug = Guid.NewGuid().ToString("N")[..8];
        var tenantA = new Tenant($"p16-a-{slug}", $"p16-a-{slug}");
        var tenantB = new Tenant($"p16-b-{slug}", $"p16-b-{slug}");
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
            dbContext.Tenants.AddRange(tenantA, tenantB);
            await dbContext.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Establish(tenantA.Id);
            dbContext.AuditEntries.Add(new AuditEntry(tenantA.Id, "seed.a"));
            await dbContext.SaveChangesAsync();
        }

        return new Harness
        {
            Provider = provider,
            Recorder = recorder,
            TenantA = tenantA,
            TenantB = tenantB,
            Worker = new BackgroundJobWorker(provider, Options.Create(_options), NullLogger<BackgroundJobWorker>.Instance),
        };
    }

    [Fact]
    public async Task Enqueue_Requires_Tenant_Context()
    {
        await using var harness = await CreateHarnessAsync();
        await using var scope = harness.Provider.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>();

        await Assert.ThrowsAsync<MissingTenantContextException>(
            () => queue.EnqueueAsync(new EnqueueBackgroundJob(TestJobHandler.JobType, "{}")));
    }

    [Fact]
    public async Task Job_Carries_Tenant_Identity_Operation_And_Correlation_Into_Execution()
    {
        await using var harness = await CreateHarnessAsync();
        var initiator = new CurrentUser(Guid.NewGuid(), harness.TenantA.Id, "user-1", "internal", IsServiceAccount: false);
        var jobId = await harness.EnqueueAsync(
            harness.TenantA.Id, "{}", idempotencyKey: null, user: initiator, correlationId: "corr-42");

        await harness.Worker.ProcessAvailableAsync();

        var context = Assert.Single(harness.Recorder.Executed);
        var job = await harness.GetJobAsync(jobId);
        Assert.Equal(harness.TenantA.Id, context.TenantId);
        Assert.Equal(job.OperationId, context.OperationId);
        Assert.Equal("corr-42", context.CorrelationId);
        Assert.Equal(1, context.Attempt);
        Assert.Equal(harness.TenantA.Id, Assert.Single(harness.Recorder.ScopedTenantIds));
        var scopedUser = Assert.Single(harness.Recorder.ScopedUsers);
        Assert.Equal(initiator.UserId, scopedUser.UserId);
        Assert.Equal("user-1", scopedUser.Subject);
        Assert.False(scopedUser.IsServiceAccount);
        Assert.Equal(harness.TenantA.Id, scopedUser.TenantId);
        Assert.Equal(BackgroundJobStatus.Completed, job.Status);
        Assert.Null(job.LastError);
        Assert.NotNull(job.StartedAtUtc);
        Assert.NotNull(job.CompletedAtUtc);
    }

    [Fact]
    public async Task System_Job_Without_Identity_Runs_As_Service_Account()
    {
        await using var harness = await CreateHarnessAsync();
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");

        await harness.Worker.ProcessAvailableAsync();

        var user = Assert.Single(harness.Recorder.ScopedUsers);
        Assert.Equal(BackgroundJobWorker.SystemUserId, user.UserId);
        Assert.True(user.IsServiceAccount);
        // A job always carries a correlation id: inherited or derived from its operation id.
        var job = await harness.GetJobAsync(jobId);
        Assert.False(string.IsNullOrWhiteSpace(job.CorrelationId));
    }

    [Fact]
    public async Task Transient_Failure_Retries_With_Backoff_Then_Succeeds()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Recorder.OnExecute = (context, _, _) =>
            context.Attempt == 1
                ? throw new IOException("temporary storage outage")
                : Task.CompletedTask;
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");

        await harness.Worker.ProcessAvailableAsync();
        var failed = await harness.GetJobAsync(jobId);
        Assert.Equal(BackgroundJobStatus.Queued, failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Contains("temporary storage outage", failed.LastError);
        Assert.True(failed.ScheduledAtUtc > DateTimeOffset.UtcNow); // back off scheduled in the future

        // Back off: not due yet, the worker must not pick it up.
        await harness.Worker.ProcessAvailableAsync();
        Assert.Single(harness.Recorder.Executed);

        await MakeDueAsync(harness, jobId);
        await harness.Worker.ProcessAvailableAsync();

        var job = await harness.GetJobAsync(jobId);
        Assert.Equal(BackgroundJobStatus.Completed, job.Status);
        Assert.Equal(2, harness.Recorder.Executed.Count);
        Assert.Equal(2, harness.Recorder.Executed[1].Attempt);
    }

    [Fact]
    public async Task Job_Goes_Dead_When_Retries_Are_Exhausted()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Recorder.OnExecute = (_, _, _) => throw new IOException("always fails");
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");

        for (var attempt = 0; attempt < _options.MaxAttempts; attempt++)
        {
            await harness.Worker.ProcessAvailableAsync();
            await MakeDueAsync(harness, jobId);
        }

        var job = await harness.GetJobAsync(jobId);
        Assert.Equal(BackgroundJobStatus.Dead, job.Status);
        Assert.Equal(_options.MaxAttempts, job.AttemptCount);
        Assert.Contains("Attempts exhausted", job.LastError);
        Assert.Equal(_options.MaxAttempts, harness.Recorder.Executed.Count);

        // Dead jobs are never picked up again.
        await harness.Worker.ProcessAvailableAsync();
        Assert.Equal(_options.MaxAttempts, harness.Recorder.Executed.Count);
    }

    [Fact]
    public async Task Permanent_Failure_Moves_Directly_To_Dead_Without_Retry()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Recorder.OnExecute = (_, _, _) => throw new PermanentJobFailureException("connector misconfigured");
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");

        await harness.Worker.ProcessAvailableAsync();

        var job = await harness.GetJobAsync(jobId);
        Assert.Equal(BackgroundJobStatus.Dead, job.Status);
        Assert.Equal(1, job.AttemptCount); // no pointless retries (#35)
        Assert.Equal("connector misconfigured", job.LastError);
    }

    [Fact]
    public async Task Unknown_Job_Type_Moves_To_Dead()
    {
        await using var harness = await CreateHarnessAsync();
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}", type: "does.not.exist");

        await harness.Worker.ProcessAvailableAsync();

        var job = await harness.GetJobAsync(jobId);
        Assert.Equal(BackgroundJobStatus.Dead, job.Status);
        Assert.Contains("No background job handler registered", job.LastError);
    }

    [Fact]
    public async Task Idempotency_Key_Prevents_Duplicate_Enqueue_And_Duplicate_Execution()
    {
        await using var harness = await CreateHarnessAsync();
        var first = await harness.EnqueueAsync(harness.TenantA.Id, "{}", idempotencyKey: "change-123");
        var second = await harness.EnqueueAsync(harness.TenantA.Id, "{}", idempotencyKey: "change-123");

        Assert.Equal(first, second);
        Assert.Equal(1, await harness.CountJobsAsync(first));

        await harness.Worker.ProcessAvailableAsync();
        Assert.Single(harness.Recorder.Executed);

        // Even re-enqueuing after completion must not execute the operation again.
        var third = await harness.EnqueueAsync(harness.TenantA.Id, "{}", idempotencyKey: "change-123");
        Assert.Equal(first, third);
        await harness.Worker.ProcessAvailableAsync();
        Assert.Single(harness.Recorder.Executed);
    }

    [Fact]
    public async Task Concurrent_Workers_Never_Execute_The_Same_Job_Twice()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Recorder.OnExecute = async (_, _, _) => await Task.Delay(300);
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");
        var secondWorker = new BackgroundJobWorker(
            harness.Provider, Options.Create(_options), NullLogger<BackgroundJobWorker>.Instance);

        await Task.WhenAll(harness.Worker.ProcessAvailableAsync(), secondWorker.ProcessAvailableAsync());

        Assert.Single(harness.Recorder.Executed);
        Assert.Equal(BackgroundJobStatus.Completed, (await harness.GetJobAsync(jobId)).Status);
    }

    [Fact]
    public async Task Cancellation_Of_A_Queued_Job_Skips_Execution()
    {
        await using var harness = await CreateHarnessAsync();
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");

        await harness.CancelAsync(harness.TenantA.Id, jobId);
        await harness.Worker.ProcessAvailableAsync();

        Assert.Empty(harness.Recorder.Executed);
        var job = await harness.GetJobAsync(jobId);
        Assert.Equal(BackgroundJobStatus.Cancelled, job.Status);
        Assert.NotNull(job.CompletedAtUtc);
    }

    [Fact]
    public async Task Cancellation_Of_A_Running_Job_Cancels_The_Handler_Token()
    {
        await using var harness = await CreateHarnessAsync();
        var handlerStarted = new TaskCompletionSource();
        harness.Recorder.OnExecute = async (_, _, cancellationToken) =>
        {
            handlerStarted.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken); // cooperative cancellation
        };
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");

        var processing = harness.Worker.ProcessAvailableAsync();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.CancelAsync(harness.TenantA.Id, jobId);
        await processing.WaitAsync(TimeSpan.FromSeconds(10));

        var job = await harness.GetJobAsync(jobId);
        Assert.Equal(BackgroundJobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task Crashed_Worker_Leaves_Running_Jobs_Which_Are_Recovered_On_Startup()
    {
        await using var harness = await CreateHarnessAsync();
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");

        // Simulate a crash between claim and execution: the row stays Running.
        await using (var scope = harness.Provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AkashaDbContext>().BackgroundJobs
                .IgnoreQueryFilters()
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(job => job.Status, BackgroundJobStatus.Running)
                    .SetProperty(job => job.StartedAtUtc, DateTimeOffset.UtcNow));
        }

        await harness.Worker.ProcessAvailableAsync();
        Assert.Empty(harness.Recorder.Executed); // not recoverable without the startup step

        await harness.Worker.RecoverAbandonedJobsAsync();
        await harness.Worker.ProcessAvailableAsync();

        Assert.Single(harness.Recorder.Executed);
        Assert.Equal(BackgroundJobStatus.Completed, (await harness.GetJobAsync(jobId)).Status);
    }

    [Fact]
    public async Task Host_Shutdown_MidRun_Leaves_The_Job_Running_For_Recovery()
    {
        await using var harness = await CreateHarnessAsync();
        var handlerStarted = new TaskCompletionSource();
        harness.Recorder.OnExecute = async (_, _, cancellationToken) =>
        {
            handlerStarted.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        var jobId = await harness.EnqueueAsync(harness.TenantA.Id, "{}");
        using var shutdown = new CancellationTokenSource();

        var processing = harness.Worker.ProcessAvailableAsync(shutdown.Token);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        // Nothing was persisted over the Running claim: the job is recoverable, not dead.
        Assert.Equal(BackgroundJobStatus.Running, (await harness.GetJobAsync(jobId)).Status);
        await harness.Worker.RecoverAbandonedJobsAsync();
        harness.Recorder.OnExecute = null;
        await harness.Worker.ProcessAvailableAsync();
        Assert.Equal(BackgroundJobStatus.Completed, (await harness.GetJobAsync(jobId)).Status);
    }

    [Fact]
    public async Task Tenant_Isolation_Holds_During_Execution_And_Cancellation()
    {
        await using var harness = await CreateHarnessAsync();

        // Tenant B owns two audit entries that tenant A's job must not see (A has exactly one).
        await using (var scope = harness.Provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Establish(harness.TenantB.Id);
            var dbContext = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
            dbContext.AuditEntries.Add(new AuditEntry(harness.TenantB.Id, "seed.b1"));
            dbContext.AuditEntries.Add(new AuditEntry(harness.TenantB.Id, "seed.b2"));
            await dbContext.SaveChangesAsync();
        }

        var jobA = await harness.EnqueueAsync(harness.TenantA.Id, "{}");
        var jobB = await harness.EnqueueAsync(harness.TenantB.Id, "{}");

        // Cross-tenant cancellation is invisible: tenant A cannot cancel tenant B's job.
        await Assert.ThrowsAsync<NotFoundException>(() => harness.CancelAsync(harness.TenantA.Id, jobB));

        await harness.Worker.ProcessAvailableAsync();

        Assert.Equal(2, harness.Recorder.Executed.Count);
        // The DbContext query filter held inside both jobs: each saw only its own tenant's rows.
        foreach (var (context, visible) in harness.Recorder.Executed.Zip(harness.Recorder.VisibleAuditEntries))
        {
            Assert.Equal(context.TenantId == harness.TenantA.Id ? 1 : 2, visible);
        }

        // A handler cannot sneak in a write for another tenant: the save guard blocks it.
        harness.Recorder.OnExecute = async (_, dbContext, _) =>
        {
            var entry = dbContext.AuditEntries.Add(new AuditEntry(harness.TenantB.Id, "cross.tenant.write"));
            await Assert.ThrowsAsync<TenantIsolationViolationException>(() => dbContext.SaveChangesAsync());
            entry.State = EntityState.Detached; // keep the job scope clean so the job itself completes
        };
        var jobA2 = await harness.EnqueueAsync(harness.TenantA.Id, "{}", idempotencyKey: "guard");
        await harness.Worker.ProcessAvailableAsync();
        Assert.Equal(BackgroundJobStatus.Completed, (await harness.GetJobAsync(jobA2)).Status);

        Assert.Equal(BackgroundJobStatus.Completed, (await harness.GetJobAsync(jobA)).Status);
        Assert.Equal(BackgroundJobStatus.Completed, (await harness.GetJobAsync(jobB)).Status);
    }
}
