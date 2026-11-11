using Horica.ClaimsService.Auth;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Horica.ClaimsService.Api;

public static class SwaggerServiceCollectionExtensions
{
    public static IServiceCollection AddClaimsSwagger(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Horica ClaimsService",
                Version = "v1",
                Description = "Damage claims: submission, status, handling history (Zavarovalnica Horica)."
            });

            var xml = Path.Combine(AppContext.BaseDirectory, "Horica.ClaimsService.xml");
            if (File.Exists(xml))
            {
                options.IncludeXmlComments(xml);
            }

            // Ids of the security schemes every operation needs (sent together).
            var schemeIds = new List<string>();

            if (auth.IsDisabled)
            {
                // Lets you switch users in Swagger ("Authorize" button) without a token.
                AddHeader(options, schemeIds, "X-Dev-User", "User id (default " + DevAuthHandler.DefaultUser + ").");
                AddHeader(options, schemeIds, "X-Dev-Roles", "Comma separated roles (default " + DevAuthHandler.DefaultRoles +
                                                             "). Customer: CLIENT. Employee: REVIEWER. Manager: ADMIN.");
                AddHeader(options, schemeIds, "X-Dev-Email", "Optional e-mail; the part before @ becomes the name.");
            }
            else
            {
                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access token from auth-service (POST /auth/login) or Keycloak."
                });
                schemeIds.Add("Bearer");
            }

            options.AddSecurityRequirement(document =>
            {
                var requirement = new OpenApiSecurityRequirement();
                foreach (var id in schemeIds)
                {
                    requirement[new OpenApiSecuritySchemeReference(id, document)] = new List<string>();
                }

                return requirement;
            });
        });

        return services;
    }

    private static void AddHeader(SwaggerGenOptions options, List<string> schemeIds, string header, string description)
    {
        options.AddSecurityDefinition(header, new OpenApiSecurityScheme
        {
            Name = header,
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Description = description
        });

        schemeIds.Add(header);
    }
}
