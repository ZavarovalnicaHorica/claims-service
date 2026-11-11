using Horica.ClaimsService.Api;
using Horica.ClaimsService.Application;
using Horica.ClaimsService.Auth;
using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Horica.ClaimsService.Infrastructure.Persistence;
using Horica.ClaimsService.Infrastructure.Policies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<AutoApprovalOptions>(builder.Configuration.GetSection(AutoApprovalOptions.SectionName));
builder.Services.Configure<PolicyOptions>(builder.Configuration.GetSection(PolicyOptions.SectionName));

builder.Services.AddClaimsPersistence(builder.Configuration);
builder.Services.AddClaimsAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddClaimsMessaging(builder.Configuration);

var policyOptions = builder.Configuration.GetSection(PolicyOptions.SectionName).Get<PolicyOptions>() ?? new PolicyOptions();
builder.Services.AddHttpClient<PolicyClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(policyOptions.TimeoutSeconds, 1, 60));
});

builder.Services.AddScoped<ClaimService>();
builder.Services.AddScoped<IncomingEventHandler>();

builder.Services
    .AddControllers()
    .AddJsonOptions(options => JsonDefaults.Configure(options.JsonSerializerOptions));

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services
    .AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

builder.Services.AddClaimsSwagger(builder.Configuration);

var app = builder.Build();

// Creates the schema (and waits for PostgreSQL) before any background worker starts.
await DatabaseInitializer.InitializeAsync(app.Services);

app.UseExceptionHandler();

var swaggerEnabled = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled");
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Liveness: the process is up. Readiness: the database is reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();

// Makes the entry point visible to integration tests (WebApplicationFactory<Program>).
public partial class Program
{
}
