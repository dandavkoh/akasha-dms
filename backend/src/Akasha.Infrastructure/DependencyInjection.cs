using Akasha.Application.Akten;
using Akasha.Application.Identity;
using Akasha.Application.Authorization;
using Akasha.Application.Documents;
using Akasha.Application.Tenancy;
using Akasha.Application.Tenants;
using Akasha.Infrastructure.Akten;
using Akasha.Infrastructure.Authorization;
using Akasha.Infrastructure.Configuration;
using Akasha.Infrastructure.Documents;
using Akasha.Infrastructure.Identity;
using Akasha.Infrastructure.Persistence;
using Akasha.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Akasha.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "Database:ConnectionString is required. Set it via the Database__ConnectionString environment variable or a secrets source.")
            .ValidateOnStart();

        services.AddOptions<CorsOptions>()
            .Bind(configuration.GetSection(CorsOptions.SectionName))
            .ValidateOnStart();

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentUser, CurrentUserContext>();
        services.AddScoped<IIdentityResolver, IdentityResolver>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<ITenantQueries, TenantQueries>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IAkteService, AkteService>();
        // Additional identity providers (Entra, OIDC, SAML, LDAP...) register alongside this fallback.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IIdentityProvider, InternalIdentityProvider>());

        services.AddDbContext<AkashaDbContext>(options => options.UseAkashaNpgsql(
            databaseOptions.ConnectionString,
            databaseOptions.CommandTimeoutSeconds));

        return services;
    }
}
