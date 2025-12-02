using TradeService.Data;
using TradeService.Messaging;
using TradeService.Telemetry;

// Enable experimental OpenTelemetry support for Azure SDK (including Service Bus)
// This must be set before creating any Azure SDK clients
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Trade Service API", Version = "v1" });
});

// Register application services
builder.Services.AddSingleton<ITradeRepository, PostgresTradeRepository>();
builder.Services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();

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
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "TradeService" }));

app.Run();

