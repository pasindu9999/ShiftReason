using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using ShiftReason.Api.Contracts;
using Xunit.Abstractions;

namespace ShiftReason.Api.Tests;

/// <summary>
/// Drives the real application: queue, background worker, solver and hub.
/// </summary>
/// <remarks>
/// Nothing is stubbed. The interesting failures in this pipeline are all timing
/// and wiring — a frame published before a client joins its group, a cancelled run
/// that discards the best roster, a worker that dies on one bad job — and none of
/// those reproduce against a mocked solver.
/// </remarks>
public sealed class SolvePipelineTests(ITestOutputHelper output) : IClassFixture<ApiFixture>, IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Health_and_presets_respond()
    {
        var client = _api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var presets = await client.GetFromJsonAsync<PresetSummary[]>("/api/presets");
        Assert.NotNull(presets);
        Assert.Contains(presets!, p => p.Id == "night-crunch");
        Assert.Contains(presets!, p => p.Id == "large-ward");
    }

    [Fact]
    public async Task Unknown_preset_is_rejected()
    {
        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/solve", new SolveRequest("no-such-ward"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A solve request must return immediately. If this ever starts taking as long
    /// as the solve itself, the work has leaked back onto the request thread.
    /// </summary>
    [Fact]
    public async Task Solve_returns_immediately_with_a_run_id()
    {
        var started = DateTimeOffset.UtcNow;

        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/solve", new SolveRequest("large-ward", Seconds: 30));

        var elapsed = DateTimeOffset.UtcNow - started;
        output.WriteLine($"POST /api/solve returned in {elapsed.TotalMilliseconds:0}ms");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<SolveAccepted>();
        Assert.False(string.IsNullOrWhiteSpace(accepted!.RunId));

        Assert.True(elapsed < TimeSpan.FromSeconds(5),
            $"enqueue took {elapsed.TotalSeconds:0.0}s — the solve is running on the request thread");

        await _api.CancelAsync(accepted.RunId);
    }

    [Fact]
    public async Task Infeasible_preset_produces_a_conflict_set_and_fixes()
    {
        var runId = await _api.SolveAsync(new SolveRequest("night-crunch", Seconds: 10));
        var run = await _api.WaitForStatusAsync(runId, "Infeasible");

        Assert.Equal("Infeasible", run.GetProperty("status").GetString());

        // The explanation is published after the result, so it needs its own wait.
        var explanation = await _api.WaitForExplanationAsync(runId);

        var conflicts = explanation.GetProperty("conflictSet");
        var fixes = explanation.GetProperty("fixes");

        foreach (var c in conflicts.EnumerateArray())
        {
            output.WriteLine("conflict: " + c.GetProperty("sentence").GetString());
        }

        Assert.True(conflicts.GetArrayLength() > 0);
        Assert.True(fixes.GetArrayLength() > 0);
        Assert.True(explanation.GetProperty("minimised").GetBoolean());

        // Plain English is the product. A blank sentence is a shipped bug.
        foreach (var c in conflicts.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("sentence").GetString()));
        }
    }

    /// <summary>The full product loop: impossible, explained, relaxed, solved.</summary>
    [Fact]
    public async Task Relaxing_the_cheapest_fix_makes_the_ward_solvable()
    {
        var firstRun = await _api.SolveAsync(new SolveRequest("night-crunch", Seconds: 10));
        await _api.WaitForStatusAsync(firstRun, "Infeasible");
        var explanation = await _api.WaitForExplanationAsync(firstRun);

        var cheapest = explanation.GetProperty("fixes")[0];
        var ruleIds = cheapest.GetProperty("rules")
            .EnumerateArray()
            .Select(r => r.GetProperty("ruleId").GetString()!)
            .ToArray();

        output.WriteLine($"relaxing {ruleIds.Length} rule(s), cost {cheapest.GetProperty("totalCost").GetInt64()}");

        var secondRun = await _api.SolveAsync(
            new SolveRequest("night-crunch", Seconds: 15, RelaxRuleIds: ruleIds, ParentRunId: firstRun));

        var run = await _api.WaitForStatusAsync(secondRun, "Completed");

        Assert.Equal(firstRun, run.GetProperty("parentRunId").GetString());
        Assert.True(run.GetProperty("objective").GetDouble() >= 0);
    }

    /// <summary>
    /// Stopping is a normal outcome, not an error: the solver must actually stop,
    /// and any roster found before the stop must survive.
    /// </summary>
    /// <remarks>
    /// The wait for a first frame matters. Cancel before CP-SAT has found anything
    /// — easy to do on a loaded machine — and there is legitimately no roster to
    /// keep, so asserting one unconditionally tests the wrong thing and fails for
    /// the wrong reason. The real invariant is the conditional one below.
    /// </remarks>
    [Fact]
    public async Task Cancelling_stops_the_solve_and_keeps_the_best_roster()
    {
        await using var connection = _api.CreateHubConnection();
        await connection.StartAsync();

        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<RosterDelta>("rosterImproved", _ => firstFrame.TrySetResult());

        var runId = await _api.SolveAsync(new SolveRequest("large-ward", Seconds: 60));
        await connection.InvokeAsync("Watch", runId);

        // Give the solver a chance to find something before pulling the plug.
        var sawSolution = await Task.WhenAny(firstFrame.Task, Task.Delay(TimeSpan.FromSeconds(45)))
                          == firstFrame.Task;

        Assert.Equal(HttpStatusCode.Accepted, (await _api.CancelAsync(runId)).StatusCode);

        var run = await _api.WaitForStatusAsync(runId, "Cancelled");

        var wall = run.GetProperty("wallSeconds").GetDouble();
        var solutions = run.GetProperty("solutionCount").GetInt32();
        output.WriteLine($"asked for 60s, stopped after {wall:0.0}s with {solutions} solution(s)");

        Assert.True(wall < 45, $"solve ran {wall:0.0}s after cancel — StopSearch did not take effect");

        if (solutions > 0)
        {
            Assert.False(string.IsNullOrEmpty(run.GetProperty("rosterJson").GetString()),
                "a roster was found before the cancel but was then discarded");
        }

        Assert.True(sawSolution || solutions == 0,
            "frames were reported but none reached the client");
    }

    /// <summary>
    /// The public endpoint that starts solves has a global budget. Rejected presets
    /// are used deliberately: the limiter runs before the handler, so they spend
    /// permits without spending any solver time, and the test stays fast.
    /// </summary>
    [Fact]
    public async Task Solve_requests_beyond_the_budget_are_rejected()
    {
        using var limited = _api.WithWebHostBuilder(b => b.UseSetting("RateLimiting:SolvesPerMinute", "2"));
        var client = limited.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/solve", new SolveRequest("no-such-ward"));
            codes.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests],
            codes);
    }

    [Fact]
    public async Task Cancelling_an_unknown_run_is_not_found()
    {
        var response = await _api.CancelAsync("does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The headline feature. Subscribes before enqueuing, because a client that
    /// joins the group late silently misses every frame.
    /// </summary>
    [Fact]
    public async Task Improving_solutions_stream_over_signalr()
    {
        await using var connection = _api.CreateHubConnection();
        await connection.StartAsync();

        var deltas = new List<RosterDelta>();
        var completed = new TaskCompletionSource<RunCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<RosterDelta>("rosterImproved", d => { lock (deltas) deltas.Add(d); });
        connection.On<RunCompleted>("runCompleted", c => completed.TrySetResult(c));

        var runId = await _api.SolveAsync(new SolveRequest("large-ward", Seconds: 12));
        await connection.InvokeAsync("Watch", runId);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(90));

        List<RosterDelta> received;
        lock (deltas) received = [.. deltas];

        output.WriteLine($"status={result.Status} objective={result.Objective} " +
                         $"solutions={result.SolutionCount} framesReceived={received.Count}");

        Assert.Equal("Completed", result.Status);
        Assert.NotEmpty(received);

        // Objective must improve monotonically — this is a minimisation, and a
        // grid that jumped backwards would mean frames arriving out of order.
        var objectives = received.OrderBy(d => d.Seq).Select(d => d.Objective).ToList();
        Assert.Equal(objectives.OrderByDescending(o => o).ToList(), objectives);

        // Frames after the first must be diffs, or the delta encoding is not working.
        Assert.True(received.Count(d => d.IsFull) <= 1,
            "more than one full-grid frame was sent; deltas are not being applied");
        Assert.All(received, d => Assert.Equal(runId, d.RunId));
    }
}

/// <summary>Boots the real app against a throwaway SQLite file.</summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"shiftreason-test-{Guid.NewGuid():n}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
        builder.UseSetting("Storage:DbPath", _dbPath);

    public HubConnection CreateHubConnection() =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hubs/solve"), options =>
            {
                // Routes the hub through the in-memory TestServer instead of a socket.
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            })
            .Build();

    public async Task<string> SolveAsync(SolveRequest request)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/solve", request);
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<SolveAccepted>(Json);
        return accepted!.RunId;
    }

    public Task<HttpResponseMessage> CancelAsync(string runId) =>
        CreateClient().PostAsync($"/api/runs/{runId}/cancel", content: null);

    public async Task<JsonElement> WaitForStatusAsync(string runId, string status, int timeoutSeconds = 120)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        var client = CreateClient();
        var last = "(never fetched)";

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/runs/{runId}");
            if (response.IsSuccessStatusCode)
            {
                var run = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
                last = run.GetProperty("status").GetString() ?? "(null)";
                if (last == status) return run;
                if (last is "Failed") Assert.Fail($"run {runId} failed: {run.GetProperty("error")}");
            }

            await Task.Delay(400);
        }

        throw new TimeoutException($"Run {runId} never reached '{status}' (last status '{last}').");
    }

    public async Task<JsonElement> WaitForExplanationAsync(string runId, int timeoutSeconds = 120)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        var client = CreateClient();

        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = JsonDocument.Parse(
                await client.GetStringAsync($"/api/runs/{runId}")).RootElement;

            if (run.TryGetProperty("explanationJson", out var json) &&
                json.ValueKind is JsonValueKind.String &&
                json.GetString() is { Length: > 0 } text)
            {
                return JsonDocument.Parse(text).RootElement.Clone();
            }

            await Task.Delay(400);
        }

        throw new TimeoutException($"Run {runId} never produced an explanation.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
