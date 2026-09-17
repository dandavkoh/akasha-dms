using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Akasha.Api.IntegrationTests;

public sealed class ApiFoundationTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Ping_responds_ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/system/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_contains_api_routes()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .ToList();
        Assert.Contains("/api/v1/system/ping", paths);
    }

    [Fact]
    public async Task Unknown_routes_return_problem_details()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Unhandled_exceptions_return_problem_details_with_correlation_id()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/system/fail");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        var correlationId = problem.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_client_sends_none()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.False(string.IsNullOrWhiteSpace(response.Headers.GetValues("X-Correlation-ID").Single()));
    }

    [Fact]
    public async Task Client_supplied_correlation_id_is_reused()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-ID", "test-correlation-123");

        var response = await client.SendAsync(request);

        Assert.Equal("test-correlation-123", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Cors_preflight_for_configured_origin_is_allowed()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/system/ping");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public void Foundation_services_resolve_from_di()
    {
        Assert.NotNull(factory.Services.GetRequiredService<ICorsService>());
        Assert.NotNull(factory.Services.GetRequiredService<HealthCheckService>());
        Assert.NotNull(factory.Services.GetRequiredService<ILoggerFactory>());
    }
}
