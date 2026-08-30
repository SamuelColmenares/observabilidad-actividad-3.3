using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using DataAccess.Data;
using DataAccess.Models;
using DataAccess.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// OpenTelemetry Configuration
// -----------------------------------------------------------------------------
static string ResolveOtlpEndpoint(string? envValue, string? configValue) =>
    !string.IsNullOrWhiteSpace(envValue) ? envValue :
    !string.IsNullOrWhiteSpace(configValue) ? configValue :
    "http://localhost:4317";

static bool ResolveObservabilityEnabled(string? envValue) =>
    !string.Equals(envValue, "false", StringComparison.OrdinalIgnoreCase);

var observabilityEnabled = ResolveObservabilityEnabled(
    Environment.GetEnvironmentVariable("OBSERVABILITY_ENABLED"));

if (observabilityEnabled)
{
    var otlpEndpoint = ResolveOtlpEndpoint(
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"),
        builder.Configuration["OpenTelemetry:OtlpEndpoint"]);

    var resourceBuilder = ResourceBuilder.CreateDefault()
        .AddService(serviceName: Diagnostics.ServiceName, serviceVersion: Diagnostics.ServiceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = Environment.GetEnvironmentVariable("DEPLOYMENT_ENVIRONMENT") ?? "local",
            ["cloud.provider"] = Environment.GetEnvironmentVariable("CLOUD_PROVIDER") ?? "local",
            ["host.name"] = Environment.MachineName
        });

    // 1. Configure OpenTelemetry Tracing with DB Semantic Conventions
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing =>
        {
            tracing
                .SetResourceBuilder(resourceBuilder)
                .AddSource(Diagnostics.ServiceName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = true;
                })
                .AddHttpClientInstrumentation(options =>
                {
                    options.RecordException = true;
                })
                // OTel DB Semantic Conventions for EF Core & Npgsql
                .AddEntityFrameworkCoreInstrumentation(options =>
                {
                    options.SetDbStatementForText = true;
                })
                .AddNpgsql()
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                    options.Protocol = OtlpExportProtocol.Grpc;
                });
        })
        // 2. Configure OpenTelemetry Metrics
        .WithMetrics(metrics =>
        {
            metrics
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(Diagnostics.ServiceName)
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter("System.Net.Http")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter((options, readerOptions) =>
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                    options.Protocol = OtlpExportProtocol.Grpc;
                    readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 2000;
                });
        });

    // 3. Configure OpenTelemetry Logging
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.FormatterName = OtelJsonConsoleFormatter.FormatterName)
        .AddConsoleFormatter<OtelJsonConsoleFormatter, ConsoleFormatterOptions>();
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.SetResourceBuilder(resourceBuilder);
        logging.IncludeScopes = true;
        logging.IncludeFormattedMessage = true;
        logging.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(otlpEndpoint);
            options.Protocol = OtlpExportProtocol.Grpc;
        });
    });
}
else
{
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = false;
        options.SingleLine = true;
    });
    Console.WriteLine("[Startup] OBSERVABILITY_ENABLED=false -> OpenTelemetry pipeline disabled.");
}

// -----------------------------------------------------------------------------
// Database Configuration (PostgreSQL EF Core)
// -----------------------------------------------------------------------------
var postgresHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
var postgresUser = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
var postgresPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
var postgresDb = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "observability_db";
var defaultConnStr = $"Host={postgresHost};Port=5432;Database={postgresDb};Username={postgresUser};Password={postgresPass}";

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? defaultConnStr;
builder.Services.AddDbContext<DataAccessDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

var app = builder.Build();

// Auto-initialize Postgres database schema with retry logic
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var dbContext = scope.ServiceProvider.GetRequiredService<DataAccessDbContext>();

    const int maxRetries = 10;
    var delay = TimeSpan.FromSeconds(3);

    for (int retry = 1; retry <= maxRetries; retry++)
    {
        try
        {
            logger.LogInformation("Attempting PostgreSQL schema initialization for DataAccess (Attempt {Retry}/{MaxRetries})...", retry, maxRetries);
            await dbContext.Database.EnsureCreatedAsync();
            logger.LogInformation("PostgreSQL database connection established and schema initialized successfully in DataAccess.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostgreSQL database initialization attempt {Retry}/{MaxRetries} failed. Retrying in {Delay}s...", retry, maxRetries, delay.TotalSeconds);
            if (retry == maxRetries)
            {
                logger.LogError(ex, "Failed to initialize PostgreSQL database schema after {MaxRetries} retries.", maxRetries);
            }
            else
            {
                await Task.Delay(delay);
            }
        }
    }
}

// -----------------------------------------------------------------------------
// Middleware: Structured Logging & Correlation ID Propagation
// -----------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    const string CorrelationHeader = "X-Correlation-ID";
    var correlationId = context.Request.Headers[CorrelationHeader].FirstOrDefault() 
                        ?? Activity.Current?.TraceId.ToString() 
                        ?? Guid.NewGuid().ToString();

    context.Response.Headers[CorrelationHeader] = correlationId;
    Activity.Current?.SetTag("correlation_id", correlationId);

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    using (logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = correlationId,
        ["TraceId"] = Activity.Current?.TraceId.ToString() ?? string.Empty
    }))
    {
        await next();
    }
});

// -----------------------------------------------------------------------------
// API Endpoints
// -----------------------------------------------------------------------------

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = Diagnostics.ServiceName,
    timestamp = DateTime.UtcNow
}));

// --- PASSENGERS ENDPOINTS ---

app.MapGet("/passengers", async (
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    DataAccessDbContext db,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("GetAllPassengers");

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.DataAccessDelayHistogram.Record(delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error simulation");
        Diagnostics.DbErrorCounter.Add(1);
        return Results.Problem(detail: "Simulated error", statusCode: 500);
    }

    Diagnostics.DbQueryCounter.Add(1);
    var passengers = await db.Passengers.ToListAsync();
    return Results.Ok(passengers);
});

app.MapGet("/passengers/{id}", async (
    string id,
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    DataAccessDbContext db,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("GetPassengerById");
    activity?.SetTag("passenger.id", id);

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.DataAccessDelayHistogram.Record(delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error simulation");
        Diagnostics.DbErrorCounter.Add(1);
        return Results.Problem(detail: "Simulated error", statusCode: 500);
    }

    Diagnostics.DbQueryCounter.Add(1);
    var passenger = await db.Passengers.FindAsync(id);
    if (passenger is null)
    {
        activity?.SetTag("passenger.found", false);
        return Results.NotFound(new { message = $"Passenger '{id}' not found." });
    }

    activity?.SetTag("passenger.found", true);
    return Results.Ok(passenger);
});

app.MapPost("/passengers", async (
    [FromBody] CreatePassengerDto dto,
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    DataAccessDbContext db,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("CreatePassengerDb");

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.DataAccessDelayHistogram.Record(delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error simulation");
        Diagnostics.DbErrorCounter.Add(1);
        return Results.Problem(detail: "Simulated error", statusCode: 500);
    }

    var passengerId = string.IsNullOrWhiteSpace(dto.Id) ? $"PAS-{Guid.NewGuid().ToString("N")[..8].ToUpper()}" : dto.Id;
    activity?.SetTag("passenger.id", passengerId);

    var passenger = new Passenger
    {
        Id = passengerId,
        FirstName = dto.FirstName,
        LastName = dto.LastName,
        Email = dto.Email,
        PassportNumber = dto.PassportNumber,
        CreatedAt = DateTime.UtcNow
    };

    db.Passengers.Add(passenger);
    await db.SaveChangesAsync();
    Diagnostics.DbQueryCounter.Add(1);

    logger.LogInformation("DataAccess: Passenger {Id} created in PostgreSQL", passengerId);
    return Results.Created($"/passengers/{passengerId}", passenger);
});

// --- CHECKINS ENDPOINTS ---

app.MapGet("/checkins", async (
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    DataAccessDbContext db,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("GetAllCheckins");

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.DataAccessDelayHistogram.Record(delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error simulation");
        Diagnostics.DbErrorCounter.Add(1);
        return Results.Problem(detail: "Simulated error", statusCode: 500);
    }

    Diagnostics.DbQueryCounter.Add(1);
    var checkins = await db.CheckinRecords.ToListAsync();
    return Results.Ok(checkins);
});

app.MapGet("/checkins/{id}", async (
    string id,
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    DataAccessDbContext db,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("GetCheckinById");
    activity?.SetTag("checkin.id", id);

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.DataAccessDelayHistogram.Record(delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error simulation");
        Diagnostics.DbErrorCounter.Add(1);
        return Results.Problem(detail: "Simulated error", statusCode: 500);
    }

    Diagnostics.DbQueryCounter.Add(1);
    var checkin = await db.CheckinRecords.FindAsync(id);
    if (checkin is null)
    {
        activity?.SetTag("checkin.found", false);
        return Results.NotFound(new { message = $"Checkin '{id}' not found." });
    }

    activity?.SetTag("checkin.found", true);
    return Results.Ok(checkin);
});

app.MapPost("/checkins", async (
    [FromBody] CreateCheckinDto dto,
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    DataAccessDbContext db,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("SaveCheckinDb");

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.DataAccessDelayHistogram.Record(delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error simulation");
        Diagnostics.DbErrorCounter.Add(1);
        return Results.Problem(detail: "Simulated error", statusCode: 500);
    }

    var checkinId = string.IsNullOrWhiteSpace(dto.Id) ? $"CHK-{Guid.NewGuid().ToString("N")[..8].ToUpper()}" : dto.Id;
    activity?.SetTag("checkin.id", checkinId);

    var record = new CheckinRecord
    {
        Id = checkinId,
        PassengerId = dto.PassengerId,
        FlightNumber = dto.FlightNumber,
        SeatNumber = dto.SeatNumber,
        BaggageCount = dto.BaggageCount,
        Status = dto.Status ?? "COMPLETED",
        CreatedAt = DateTime.UtcNow
    };

    db.CheckinRecords.Add(record);
    await db.SaveChangesAsync();
    Diagnostics.DbQueryCounter.Add(1);

    logger.LogInformation("DataAccess: Checkin {Id} saved in PostgreSQL for passenger {PassengerId}", checkinId, dto.PassengerId);
    return Results.Created($"/checkins/{checkinId}", record);
});

app.Run();
