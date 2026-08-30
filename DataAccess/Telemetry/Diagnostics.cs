using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DataAccess.Telemetry;

/// <summary>
/// Holds OpenTelemetry ActivitySource and Meter metrics definitions for DataAccess service.
/// </summary>
public static class Diagnostics
{
    public const string ServiceName = "data-access-service";
    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static readonly Counter<long> DbQueryCounter = Meter.CreateCounter<long>(
        "dataaccess.queries.count",
        description: "Number of queries executed by DataAccess service");

    public static readonly Counter<long> DbErrorCounter = Meter.CreateCounter<long>(
        "dataaccess.errors.count",
        description: "Number of database errors in DataAccess service");

    public static readonly Histogram<double> DbOperationDurationHistogram = Meter.CreateHistogram<double>(
        "dataaccess.operation.duration.ms",
        unit: "ms",
        description: "Duration of DataAccess operations in milliseconds");

    public static readonly Histogram<double> DataAccessDelayHistogram = Meter.CreateHistogram<double>(
        "dataaccess.request.delay.ms",
        unit: "ms",
        description: "Artificial response delays simulated in milliseconds");
}
