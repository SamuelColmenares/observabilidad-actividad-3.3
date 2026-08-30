using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Passengers.Models;
using Passengers.Telemetry;

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

    // 1. Configure OpenTelemetry Tracing
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
// HttpClient Configuration (DataAccess Microservice)
// -----------------------------------------------------------------------------
var dataAccessUrl = Environment.GetEnvironmentVariable("DATA_ACCESS_URL") 
                    ?? builder.Configuration["Services:DataAccessUrl"] 
                    ?? "http://localhost:5003";

builder.Services.AddHttpClient("DataAccessClient", client =>
{
    client.BaseAddress = new Uri(dataAccessUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

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

/// <summary>
/// GET /passengers - List all passengers via DataAccess service.
/// </summary>
app.MapGet("/passengers", async (
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    IHttpClientFactory httpClientFactory,
    HttpContext httpContext,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("GetAllPassengers");

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.PassengerDelayHistogram.Record(delay.Value);
        logger.LogInformation("Simulating artificial delay of {Delay} ms for GET /passengers", delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetTag("simulation.forced_error", true);
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error requested via query parameter");
        Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "GET /passengers"));
        logger.LogError("Forced error triggered for GET /passengers");
        return Results.Problem(detail: "Simulated error", statusCode: 500);
    }

    var client = httpClientFactory.CreateClient("DataAccessClient");
    var correlationId = httpContext.Response.Headers["X-Correlation-ID"].FirstOrDefault();
    if (!string.IsNullOrEmpty(correlationId))
    {
        client.DefaultRequestHeaders.Remove("X-Correlation-ID");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
    }

    try
    {
        var response = await client.GetAsync("/passengers");
        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        activity?.AddException(ex);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "GET /passengers"));
        logger.LogError(ex, "Error communicating with DataAccess service");
        return Results.Problem(detail: ex.Message, statusCode: 502);
    }
});

/// <summary>
/// GET /passengers/{id} - Retrieve passenger information via DataAccess microservice.
/// Supports simulation parameters: ?delay=ms and ?error=true.
/// </summary>
app.MapGet("/passengers/{id}", async (
    string id,
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    IHttpClientFactory httpClientFactory,
    HttpContext httpContext,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("GetPassengerById");
    activity?.SetTag("passenger.id", id);

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.PassengerDelayHistogram.Record(delay.Value);
        logger.LogInformation("Simulating artificial delay of {Delay} ms for GET /passengers/{Id}", delay.Value, id);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetTag("simulation.forced_error", true);
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error requested via query parameter");
        Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "GET /passengers/{id}"));
        logger.LogError("Forced error triggered for GET /passengers/{Id}", id);
        return Results.Problem(
            detail: "Forced error simulated as requested by parameter error=true.",
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Simulated Internal Server Error");
    }

    Diagnostics.PassengerRetrievedCounter.Add(1);
    logger.LogInformation("Fetching passenger with ID {Id} from DataAccess service", id);

    var client = httpClientFactory.CreateClient("DataAccessClient");
    var correlationId = httpContext.Response.Headers["X-Correlation-ID"].FirstOrDefault();
    if (!string.IsNullOrEmpty(correlationId))
    {
        client.DefaultRequestHeaders.Remove("X-Correlation-ID");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
    }

    try
    {
        var response = await client.GetAsync($"/passengers/{Uri.EscapeDataString(id)}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            activity?.SetTag("passenger.found", false);
            logger.LogWarning("Passenger with ID {Id} not found in DataAccess", id);
            return Results.NotFound(new { message = $"Passenger '{id}' not found." });
        }

        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, $"DataAccess returned {response.StatusCode}");
            Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "GET /passengers/{id}"));
            return Results.Problem(detail: $"DataAccess returned HTTP {response.StatusCode}", statusCode: (int)response.StatusCode);
        }

        var passenger = await response.Content.ReadFromJsonAsync<Passenger>();
        activity?.SetTag("passenger.found", true);
        return Results.Ok(passenger);
    }
    catch (Exception ex)
    {
        activity?.AddException(ex);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "GET /passengers/{id}"));
        logger.LogError(ex, "Error occurred while fetching passenger {Id} from DataAccess", id);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

/// <summary>
/// POST /passengers - Create a new passenger record via DataAccess microservice.
/// Supports simulation parameters: ?delay=ms and ?error=true.
/// </summary>
app.MapPost("/passengers", async (
    [FromBody] CreatePassengerDto dto,
    [FromQuery] int? delay,
    [FromQuery] bool? error,
    IHttpClientFactory httpClientFactory,
    HttpContext httpContext,
    ILogger<Program> logger) =>
{
    using var activity = Diagnostics.ActivitySource.StartActivity("CreatePassenger");

    if (delay.HasValue && delay.Value > 0)
    {
        activity?.SetTag("simulation.delay_ms", delay.Value);
        Diagnostics.PassengerDelayHistogram.Record(delay.Value);
        logger.LogInformation("Simulating artificial delay of {Delay} ms for POST /passengers", delay.Value);
        await Task.Delay(delay.Value);
    }

    if (error == true)
    {
        activity?.SetTag("simulation.forced_error", true);
        activity?.SetStatus(ActivityStatusCode.Error, "Forced error requested via query parameter");
        Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "POST /passengers"));
        logger.LogError("Forced error triggered for POST /passengers");
        return Results.Problem(
            detail: "Forced error simulated as requested by parameter error=true.",
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Simulated Internal Server Error");
    }

    var passengerId = string.IsNullOrWhiteSpace(dto.Id) ? $"PAS-{Guid.NewGuid().ToString("N")[..8].ToUpper()}" : dto.Id;
    activity?.SetTag("passenger.id", passengerId);

    var client = httpClientFactory.CreateClient("DataAccessClient");
    var correlationId = httpContext.Response.Headers["X-Correlation-ID"].FirstOrDefault();
    if (!string.IsNullOrEmpty(correlationId))
    {
        client.DefaultRequestHeaders.Remove("X-Correlation-ID");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
    }

    try
    {
        var payload = dto with { Id = passengerId };
        logger.LogInformation("Sending new passenger {Id} to DataAccess service", passengerId);

        var response = await client.PostAsJsonAsync("/passengers", payload);
        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, $"DataAccess returned {response.StatusCode}");
            Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "POST /passengers"));
            return Results.Problem(detail: $"DataAccess returned HTTP {response.StatusCode}", statusCode: (int)response.StatusCode);
        }

        var created = await response.Content.ReadFromJsonAsync<Passenger>();
        Diagnostics.PassengerCreatedCounter.Add(1);
        logger.LogInformation("Passenger {Id} created successfully via DataAccess", passengerId);

        return Results.Created($"/passengers/{passengerId}", created);
    }
    catch (Exception ex)
    {
        activity?.AddException(ex);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        Diagnostics.PassengerErrorCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "POST /passengers"));
        logger.LogError(ex, "Error occurred while creating passenger {Id} via DataAccess", passengerId);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();
