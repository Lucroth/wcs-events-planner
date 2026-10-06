using System.Globalization;
using Google.Cloud.Firestore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Crawl;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Metrics;
using WcsEvents.Sync.Publish;
using WcsEvents.Sync.Scoring;
using WcsEvents.Sync.Travel;
using WcsEvents.Sync.Wsdc;

// Usage: wcs-sync <sweep|refresh|scoring|publish|flights|trains|autofill> [--minutes N]
//   sweep / refresh  mirror the WSDC registry into the local SQLite file (resumable; stops after N minutes)
//   scoring          mirror scoring.dance (run after the registry, or prelim roles stay unknown)
//   publish          write events, strengths and results to Firestore
//   flights          write Ryanair/Wizz fares for upcoming events abroad to Firestore
//   trains           write koleo train fares for upcoming events in Poland to Firestore
//   autofill         fill empty admin fields from data/autofill.json (details read off event websites)
// Firestore needs FIREBASE_PROJECT_ID and GOOGLE_APPLICATION_CREDENTIALS (or FIRESTORE_EMULATOR_HOST).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

if (args.FirstOrDefault() is not ("sweep" or "refresh" or "scoring" or "publish" or "flights" or "trains" or "autofill") || args.Length is not (1 or 3))
{
    Console.Error.WriteLine("usage: wcs-sync <sweep|refresh|scoring|publish|flights|trains|autofill> [--minutes N]");
    return 2;
}

var command = args[0];
TimeSpan? budget = args is [_, "--minutes", var m] && int.TryParse(m, out var minutes) ? TimeSpan.FromMinutes(minutes) : null;

// appsettings.json is copied next to the binary; CI runs `dotnet run --project` from the repo root,
// where the default content root (the working directory) would not find it.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });

var database = new SqliteConnectionStringBuilder(builder.Configuration.GetConnectionString("Db") ?? "Data Source=wcs-events.db");
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(database.ConnectionString));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);

var crawlOptions = builder.Configuration.GetSection("Crawl").Get<CrawlOptions>() ?? new CrawlOptions();
builder.Services.AddSingleton(crawlOptions);
builder.Services.AddSingleton<RateGate>();
builder.Services.AddTransient<PoliteHttpHandler>();

builder.Services.AddHttpClient<WsdcClient>(http =>
{
    http.BaseAddress = new Uri("https://points.worldsdc.com/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd(crawlOptions.UserAgent);
    http.Timeout = TimeSpan.FromMinutes(5);
}).AddHttpMessageHandler<PoliteHttpHandler>();

builder.Services.AddHttpClient<ScoringClient>(http =>
{
    http.BaseAddress = new Uri("https://scoring.dance/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd(crawlOptions.UserAgent);
    http.Timeout = TimeSpan.FromMinutes(5);
}).AddHttpMessageHandler<PoliteHttpHandler>();

// The airlines' sites are browser-facing and answer a browser User-Agent. Each gets a budget of its
// own, so a day's fare run never looks like a scrape. Wizz's bot protection starts refusing after a
// couple of quick requests, so it gets one every ten seconds.
const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
var ryanairGate = new RateGate(new CrawlOptions { RequestsPerSecond = 2 });
var wizzGate = new RateGate(new CrawlOptions { RequestsPerSecond = 0.1 });

builder.Services.AddHttpClient<RyanairClient>(http =>
{
    http.BaseAddress = new Uri("https://www.ryanair.com/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserAgent);
    http.Timeout = TimeSpan.FromMinutes(2);
}).AddHttpMessageHandler(sp => new PoliteHttpHandler(ryanairGate, sp.GetRequiredService<ILogger<PoliteHttpHandler>>()));

builder.Services.AddHttpClient<WizzClient>(http =>
{
    http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserAgent);
    http.DefaultRequestHeaders.Add("Origin", "https://www.wizzair.com");
    http.DefaultRequestHeaders.Referrer = new Uri("https://www.wizzair.com/");
    http.Timeout = TimeSpan.FromMinutes(5);
}).AddHttpMessageHandler(sp => new PoliteHttpHandler(wizzGate, sp.GetRequiredService<ILogger<PoliteHttpHandler>>()));

// koleo prices one connection per request; a gentle pace keeps a daily run under ten minutes.
var koleoGate = new RateGate(new CrawlOptions { RequestsPerSecond = 1 });

builder.Services.AddHttpClient<KoleoClient>(http =>
{
    http.BaseAddress = new Uri("https://koleo.pl/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserAgent);
    http.DefaultRequestHeaders.Add("X-KOLEO-Version", "1");
    http.DefaultRequestHeaders.Add("X-KOLEO-Client", "Nuxt-1");
    http.Timeout = TimeSpan.FromMinutes(2);
}).AddHttpMessageHandler(sp => new PoliteHttpHandler(koleoGate, sp.GetRequiredService<ILogger<PoliteHttpHandler>>()));

builder.Services.AddHttpClient("travel", http =>
{
    http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserAgent);
    http.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("nominatim", http =>
{
    http.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd(crawlOptions.UserAgent);
    http.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddSingleton<Places>();
builder.Services.AddScoped<FlightSearch>();
builder.Services.AddScoped<ScoringSync>();
builder.Services.AddSingleton<CrawlerService>();
builder.Services.AddScoped<Strength>();
builder.Services.AddScoped<EventCatalog>();
builder.Services.AddScoped<EventPublisher>();
builder.Services.AddScoped<FlightPublisher>();
builder.Services.AddScoped<TrainPublisher>();
builder.Services.AddScoped<AutofillPublisher>();
builder.Services.AddScoped<FirestoreStore>();
builder.Services.AddSingleton(_ => new FirestoreDbBuilder
{
    ProjectId = Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
        ?? throw new InvalidOperationException("FIREBASE_PROJECT_ID is not set."),
    EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction,
}.Build());

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;

var db = services.GetRequiredService<AppDbContext>();
await db.Database.MigrateAsync();
await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

using var timeLimit = budget is { } b ? new CancellationTokenSource(b) : new CancellationTokenSource();
using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, timeLimit.Token);

switch (command)
{
    case "sweep":
        await services.GetRequiredService<CrawlerService>().RunAsync(CrawlCommand.Sweep, linked.Token);
        break;
    case "refresh":
        await services.GetRequiredService<CrawlerService>().RunAsync(CrawlCommand.Refresh, linked.Token);
        break;
    case "scoring":
        await services.GetRequiredService<CrawlerService>().RunAsync(CrawlCommand.Scoring, stop.Token);
        break;
    case "publish":
        await services.GetRequiredService<EventPublisher>().PublishAsync(stop.Token);
        break;
    case "flights":
        await services.GetRequiredService<FlightPublisher>().PublishAsync(stop.Token);
        break;
    case "trains":
        await services.GetRequiredService<TrainPublisher>().PublishAsync(stop.Token);
        break;
    case "autofill":
        await services.GetRequiredService<AutofillPublisher>().PublishAsync(Path.Combine("data", "autofill.json"), stop.Token);
        break;
}

return 0;
