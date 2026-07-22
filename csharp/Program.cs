using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/adquery-orchestrator-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddLlmProviderConfiguration(builder.Configuration);
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "AdQuery Orchestrator API",
        Version = "v1",
        Description = "Directory plan-based Active Directory query orchestrator"
    });
});

builder.Services.AddMemoryCache();

builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole("ANALOG\\ADEXNLQ_Users")
        .Build();
});

// Add HTTP client for Claude API
builder.Services.AddHttpClient<IClaudeService, ClaudeService>(client =>
{
    var baseUrl = builder.Configuration["Claude:BaseUrl"] ?? "https://api.anthropic.com";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Register application services
builder.Services.AddSingleton<IPlanPreprocessor, PlanPreprocessor>();
builder.Services.AddScoped<IActiveDirectoryService, ActiveDirectoryService>();
builder.Services.AddScoped<IDirectoryPlanExecutor, DirectoryPlanExecutor>();
builder.Services.AddScoped<IPlanValidator, PlanValidator>();
builder.Services.AddScoped<ICsvEnrichmentService, CsvEnrichmentService>();

// Register job infrastructure (async query support)
builder.Services.AddSingleton<IQueryJobStore, InMemoryQueryJobStore>();
builder.Services.AddSingleton<IQueryJobQueue, InMemoryQueryJobQueue>();
builder.Services.AddSingleton<IQueryJobManager, QueryJobManager>();
builder.Services.AddHostedService<QueryJobExecutorHostedService>();

// Register feedback storage
builder.Services.AddSingleton<IFeedbackStore, JsonLinesFeedbackStore>();

// Add health checks
builder.Services.AddHealthChecks()
    .AddCheck<ClaudeHealthCheck>("claude")
    .AddCheck<OrchestratorHealthCheck>("directory_plan");

var allowedCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins")
    .GetChildren()
    .Select(origin => origin.Value?.Trim())
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .Select(origin => origin!)
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedCorsOrigins.Length > 0)
        {
            policy.WithOrigins(allowedCorsOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "AdQuery Orchestrator API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapControllers();
app.MapFallbackToFile("index.html");

Log.Information("Starting AdQuery Orchestrator API");
Log.Information("Environment: {Environment}", app.Environment.EnvironmentName);
Log.Information("Claude API configured: {Configured}", !string.IsNullOrEmpty(builder.Configuration["Claude:ApiKey"]));

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
