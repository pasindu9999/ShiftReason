using Microsoft.EntityFrameworkCore;
using ShiftReason.Api.Contracts;
using ShiftReason.Api.Persistence;
using ShiftReason.Api.Solving;
using ShiftReason.Domain;
using ShiftReason.Solver;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["Storage:DbPath"] ?? "shiftreason.db";

builder.Services.AddDbContext<RunStore>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<SolveQueue>();
builder.Services.AddHostedService<SolveWorker>();

builder.Services.AddSignalR(options =>
{
    // Frames are cell diffs, not whole grids, so the default 32 KB is ample.
    // Raised only enough to absorb the first full-grid frame of a large ward.
    options.MaximumReceiveMessageSize = 256 * 1024;
});

// The Vite dev server runs on a different origin. AllowCredentials is required
// for SignalR's WebSocket handshake, and credentials forbid a wildcard origin.
const string DevCors = "dev";
builder.Services.AddCors(options => options.AddPolicy(DevCors, policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

await InitialiseDatabaseAsync(app);

if (app.Environment.IsDevelopment()) app.UseCors(DevCors);

// The API and the SPA ship in one container, so the built frontend is served
// from here and there is no cross-origin problem in production.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<SolveHub>("/hubs/solve");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/presets", () => WardPresets.Ids.Select(id =>
{
    var s = WardPresets.ById(id);
    return new PresetSummary(
        s.Id,
        s.Name,
        s.Employees.Count,
        s.HorizonDays,
        s.ShiftTypes.Count - 1, // excluding OFF
        s.Employees.Count * s.HorizonDays * s.ShiftTypes.Count);
}));

/// Enqueues a solve. Returns immediately with a run id; results arrive on the hub.
app.MapPost("/api/solve", (SolveRequest request, SolveQueue queue) =>
{
    if (!WardPresets.Ids.Contains(request.PresetId))
    {
        return Results.BadRequest(new { error = $"Unknown preset '{request.PresetId}'." });
    }

    var runId = Guid.NewGuid().ToString("n")[..12];

    // Capped server-side: an unbounded budget lets one request occupy the single
    // worker indefinitely and starve everyone behind it.
    var seconds = Math.Clamp(request.Seconds ?? 30, 1, 60);

    var job = new SolveJob(
        runId,
        request.PresetId,
        seconds,
        request.Seed,
        (request.RelaxRuleIds ?? []).ToHashSet(StringComparer.Ordinal),
        request.ParentRunId,
        ExplainIfInfeasible: true);

    return queue.TryEnqueue(job)
        ? Results.Accepted($"/api/runs/{runId}", new SolveAccepted(runId, request.PresetId))
        : Results.Json(new { error = "Solver queue is full, try again shortly." }, statusCode: 503);
});

app.MapPost("/api/runs/{runId}/cancel", (string runId, SolveQueue queue) =>
    queue.Cancel(runId)
        ? Results.Accepted()
        : Results.NotFound(new { error = "No running solve with that id." }));

app.MapGet("/api/runs/{runId}", async (string runId, RunStore db) =>
{
    var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapGet("/api/runs", async (RunStore db, int? limit) =>
    await db.Runs.AsNoTracking()
        .OrderByDescending(r => r.CreatedUtc)
        .Take(Math.Clamp(limit ?? 25, 1, 100))
        .Select(r => new
        {
            r.Id, r.ScenarioId, r.ScenarioName, r.Status, r.Mode, r.Seed,
            r.Objective, r.WallSeconds, r.SolutionCount, r.ParentRunId,
            r.InputHash, r.CreatedUtc,
        })
        .ToListAsync());

/// The recorded frames of a run — also how demo replay traces get exported.
app.MapGet("/api/runs/{runId}/trace", async (string runId, RunStore db) =>
{
    var frames = await db.Frames.AsNoTracking()
        .Where(f => f.RunId == runId)
        .OrderBy(f => f.Seq)
        .ToListAsync();

    return frames.Count == 0 ? Results.NotFound() : Results.Ok(frames);
});

// SPA fallback: any unmatched non-API path serves the React shell.
app.MapFallbackToFile("index.html");

app.Run();

static async Task InitialiseDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RunStore>();

    await db.Database.EnsureCreatedAsync();

    // WAL plus a busy timeout. The background worker writes while requests read,
    // and SQLite's default rollback journal turns that into
    // "SQLite Error 5: database is locked" under even light concurrency.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
}

/// <summary>Exposed so the integration tests can spin the real app up.</summary>
public partial class Program;
