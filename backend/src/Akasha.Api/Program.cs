using Akasha.Api.Middleware;
using Akasha.Infrastructure;
using Akasha.Infrastructure.Configuration;
using Akasha.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

var corsOptions = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();

builder.Services.AddControllers();
builder.Services.AddCors(options => options.AddPolicy("Default", policy =>
    policy.WithOrigins(corsOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    if (context.HttpContext.Items[CorrelationIdMiddleware.ItemKey] is string correlationId)
    {
        context.ProblemDetails.Extensions["correlationId"] = correlationId;
    }
});

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await app.Services.MigrateAkashaDatabaseAsync();
}

app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.MapOpenApi();
}
else if (app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();
app.UseCors("Default");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantResolutionMiddleware>();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
