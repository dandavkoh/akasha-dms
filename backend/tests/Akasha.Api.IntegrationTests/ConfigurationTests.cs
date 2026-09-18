using Akasha.Infrastructure;
using Akasha.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Akasha.Api.IntegrationTests;

public sealed class ConfigurationTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    [Fact]
    public void Database_options_are_bound_and_validated_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=db;Database=akasha;Username=akasha",
                ["Database:CommandTimeoutSeconds"] = "15"
            })
            .Build();

        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!;

        Assert.Equal("Host=db;Database=akasha;Username=akasha", options.ConnectionString);
        Assert.Equal(15, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void Database_options_use_defaults_when_not_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=db;Database=akasha;Username=akasha"
            })
            .Build();

        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!;

        Assert.Equal(30, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void Missing_database_connection_string_fails_options_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = ""
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);
    }

    [Fact]
    public void Environment_variable_style_override_reaches_validated_options()
    {
        var options = factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        Assert.Equal(factory.ConnectionString, options.ConnectionString);
    }

    [Fact]
    public void Cors_options_bind_allowed_origins()
    {
        var options = factory.Services.GetRequiredService<IOptions<CorsOptions>>().Value;

        Assert.Contains("http://localhost:5173", options.AllowedOrigins);
        Assert.True(options.IsOriginAllowed(new Uri("http://localhost:5173/app")));
        Assert.False(options.IsOriginAllowed(new Uri("http://evil.example.com")));
    }
}
