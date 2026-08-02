using Kart.Shared.Auditing;
using Kart.Shared.Configuration;
using Kart.Shared.ErrorHandling;
using Kart.Shared.Observability;
using KartOrderService.Api;
using KartOrderService.Api.HealthChecks;
using KartOrderService.Api.Security;
using KartOrderService.Application;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Infrastructure;
using KartOrderService.Infrastructure.Auditing;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// kart-conventions.md Configuration Management: GlobalConfig external-secrets-file bootstrap,
// shared across every service - never reimplemented per service. See appsettings.Local.json.example.
// Must run before AddKartObservability below, since observability's own LogFile:Directory setting
// is read from the layered-in GlobalConfig file too.
builder.AddKartGlobalConfig();

// kart-conventions.md Observability section: Serilog + OpenTelemetry SDK behind one DI call.
// Order is one of the platform's 100%-trace-coverage services (requirement-spec.md's Observability
// NFR row - the Saga orchestrator itself, one of the four Order-Saga participants named by
// kart-conventions.md). No extra sampler configuration is needed to get there: the OpenTelemetry
// SDK's own default (ParentBased(AlwaysOnSampler)) already samples 100% of traces unless a service
// explicitly dials it down, which this one deliberately does not.
builder.AddKartObservability("kart-order-service");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOrderAuthentication();
builder.Services.AddAuthorization();

// kart-conventions.md Error Handling section: the single global exception handler +
// ProblemDetails factory, wired once via the shared package - no local try/catch for translation
// anywhere in this service's handler/controller/domain code.
builder.Services.AddKartErrorHandling(options => options
    .Map<ConcurrencyConflictException>(StatusCodes.Status409Conflict, "conflict")
    .Map<DuplicateKeyException>(StatusCodes.Status409Conflict, "conflict"));

// Order is money/saga-adjacent enough to warrant a real audit sink, not the NullAuditLogWriter
// default (EfAuditLogWriter's own remarks - this is the first service on the platform to do so).
builder.Services.AddKartAuditing<EfAuditLogWriter>();

builder.Services.AddHealthChecks()
    .AddCheck<OrderDbHealthCheck>("postgres")
    .AddCheck<OrderReadModelHealthCheck>("mongo");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Per-HTTP-request Information log (method/path/status/elapsed) - registered outermost, wrapping
// UseKartErrorHandling below, so this always logs the *final* status code a client actually
// received.
app.UseSerilogRequestLogging();

// The single global error handler - every unhandled exception is translated to the platform's
// ProblemDetails envelope and logged here.
app.UseKartErrorHandling();

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Prometheus scrape target (observability-standards.md's mandatory /metrics).
app.MapPrometheusScrapingEndpoint();

app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });

app.MapControllers();

await StartupConnectivityChecks.RunAsync(app);

app.Run();

// Exposed for WebApplicationFactory<Program> in IntegrationTests/ContractTests.
public partial class Program
{
}
