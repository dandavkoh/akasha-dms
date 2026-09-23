using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Akasha.Api.IntegrationTests;

public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    /// <summary>Configured shared filesystem connector (tenant-unrestricted) for file-operation tests.</summary>
    public static readonly Guid StorageConnectorId = Guid.Parse("f10e0000-0000-4000-8000-000000000001");

    public string StorageRoot { get; } = Directory.CreateTempSubdirectory("akasha-api-tests-").FullName;

    public string ConnectionString => _postgres.GetConnectionString();

    public Task InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
        builder.UseSetting("Database:ConnectionString", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Storage:Connectors:0:Id", StorageConnectorId.ToString());
        builder.UseSetting("Storage:Connectors:0:Type", "LocalFileSystem");
        builder.UseSetting("Storage:Connectors:0:RootPath", StorageRoot);

        builder.ConfigureTestServices(services =>
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }
}
