using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SettlementService.Telemetry;

/// <summary>
/// Extension methods for configuring OpenTelemetry instrumentation
/// </summary>
public static class TelemetryExtensions
{
    public const string ServiceName = "SettlementService";
    public const string ServiceVersion = "1.0.0";
    
    // Custom ActivitySource for manual instrumentation
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://localhost:4317";
        
        // Build resource with service information
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: ServiceName,
                serviceVersion: ServiceVersion,
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = configuration["ENVIRONMENT"] ?? "development"
            });

        // Configure OpenTelemetry Tracing
        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(resourceBuilder)
                    // Out-of-the-box instrumentation
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                    })
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                    })
                    // Azure SDK has built-in Activity/DiagnosticSource support
                    .AddSource("Azure.*")
                    // Npgsql has built-in OpenTelemetry support
                    .AddSource("Npgsql")
                    // Our custom ActivitySource for settlement operations and trace reconstruction
                    .AddSource(ServiceName)
                    // Export to OTLP endpoint
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                    });
            })
            .WithMetrics(builder =>
            {
                builder
                    .SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // Custom metrics
                    .AddMeter(ServiceName)
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                    });
            });

        // Configure logging to use OpenTelemetry
        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resourceBuilder);
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.AddOtlpExporter(exporterOptions =>
                {
                    exporterOptions.Endpoint = new Uri(otlpEndpoint);
                });
            });
        });

        return services;
    }
}

