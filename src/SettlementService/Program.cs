using SettlementService.Data;
using SettlementService.Messaging;
using SettlementService.Telemetry;

// Enable experimental OpenTelemetry support for Azure SDK (including Service Bus)
// This must be set before creating any Azure SDK clients
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Settlement Service API", Version = "v1" });
});

// Register application services
builder.Services.AddScoped<ITradeRepository, PostgresTradeRepository>();

// Register the background message processor
builder.Services.AddHostedService<TradeMessageProcessor>();

// Add OpenTelemetry instrumentation
builder.Services.AddTelemetry(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "SettlementService" }));

app.Run();

