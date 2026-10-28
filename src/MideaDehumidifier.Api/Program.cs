using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MideaDehumidifier.Api.Midea;
using MideaDehumidifier.Api.Models;
using MideaDehumidifier.Api.Services;
using MideaDehumidifier.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

builder.Services.Configure<MideaOptions>(builder.Configuration.GetSection(MideaOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var path = sp.GetRequiredService<IOptions<MideaOptions>>().Value.DatabasePath;
    if (!Path.IsPathRooted(path)) path = Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, path);
    return new ReadingStore(path);
});
builder.Services.AddHttpClient<MideaCloudClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<MideaDeviceClient>();
builder.Services.AddSingleton<DeviceState>();
builder.Services.AddHostedService<DehumidifierPoller>();

builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Midea Dehumidifier API";
    document.Info.Version = "v1";
    document.Info.Description =
        "Humidity history of a Midea dehumidifier, polled over the LAN and stored in SQLite. " +
        "Each reading carries a `band` (humidity vs. setpoint) and `powerOn` for colouring and dashing the plot.";
    return Task.CompletedTask;
}));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(); // wwwroot/index.html: the humidity plot
app.MapOpenApi();     // GET /openapi/v1.json

var api = app.MapGroup("/api").WithTags("Dehumidifier");

api.MapGet("/status", (DeviceState state) =>
        state.Latest is { } r ? Results.Ok(ReadingDto.From(r)) : NotReady(state))
    .WithName("GetStatus")
    .WithSummary("Latest reading")
    .Produces<ReadingDto>()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/readings", (ReadingStore store, TimeProvider time, double? hours, DateTimeOffset? from, DateTimeOffset? to, int? maxPoints) =>
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end - TimeSpan.FromHours(hours ?? 24);
        if (start >= end) return Results.Problem("'from' must be earlier than 'to'.", statusCode: StatusCodes.Status400BadRequest);
        if (end - start > TimeSpan.FromDays(3660)) return Results.Problem("Range is too large.", statusCode: StatusCodes.Status400BadRequest);

        var all = store.GetRange(start, end);
        var sampled = ReadingSampler.Downsample(all, Math.Clamp(maxPoints ?? 4000, 100, 50000));
        return Results.Ok(new ReadingsResponse
        {
            FromUtc = start,
            ToUtc = end,
            TotalCount = all.Count,
            Readings = sampled.Select(ReadingDto.From).ToList(),
        });
    })
    .WithName("GetReadings")
    .WithSummary("Humidity history")
    .WithDescription("Stored readings, oldest first. Select the range with `hours` (default 24, ending now) or explicit " +
                     "`from`/`to` timestamps. Long ranges are downsampled to about `maxPoints` (default 4000) while " +
                     "keeping every change of band or power state.")
    .Produces<ReadingsResponse>()
    .ProducesProblem(StatusCodes.Status400BadRequest);

api.MapGet("/health", (DeviceState state, IOptions<MideaOptions> opt, TimeProvider time) =>
    {
        var age = state.Latest is { } r ? time.GetUtcNow() - r.TimestampUtc : (TimeSpan?)null;
        var stale = age is null || age > TimeSpan.FromSeconds(opt.Value.StaleAfterSeconds);
        return Results.Ok(new HealthResponse
        {
            Status = stale ? "degraded" : "ok",
            LastReadingUtc = state.Latest?.TimestampUtc,
            AgeSeconds = age is { } a ? Math.Round(a.TotalSeconds) : null,
            LastError = state.LastError,
            LastErrorAtUtc = state.LastErrorAtUtc,
        });
    })
    .WithTags("Health")
    .WithName("Health")
    .WithSummary("Backend health")
    .Produces<HealthResponse>();

app.Run();

static IResult NotReady(DeviceState state) =>
    Results.Problem(
        state.LastError is { } err ? $"No reading yet. Last error: {err}" : "No reading yet; first poll in progress.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

public partial class Program;
