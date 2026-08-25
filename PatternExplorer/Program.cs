using System.Text.Json;
using PatternExplorer;

var repoRoot = Catalog.FindRepoRoot();
var patternsDir = Path.Combine(repoRoot, "PatternExplorer", "patterns");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5080");

var app = builder.Build();
app.UseDefaultFiles();
// Local authoring tool: never cache, so edits to the page or the pattern files show up on refresh.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store"
});

// EnvironmentAllowlist is server-side plumbing (what the child process is allowed to inherit) -
// project it out of the wire shape rather than exposing "what does the server forward" to the page.
object ProjectForWire(PatternProject p) => new { p.Flavor, p.Path, p.Interactive, p.Server, p.ServerPort, p.Note };

app.MapGet("/api/patterns", () => Catalog.Load(patternsDir)
    .Select(p => new { p.Id, p.Meta.Title, p.Meta.Summary, p.Meta.Category, Projects = p.Meta.Projects.Select(ProjectForWire), p.Meta.Risk }));

app.MapGet("/api/patterns/{id}", (string id) =>
{
    var pattern = Catalog.Load(patternsDir).FirstOrDefault(p => p.Id == id);
    if (pattern is null) return Results.NotFound();

    return Results.Ok(new
    {
        pattern.Id,
        pattern.Meta.Title,
        pattern.Meta.Summary,
        pattern.Meta.Category,
        Projects = pattern.Meta.Projects.Select(ProjectForWire),
        pattern.Meta.Risk,
        pattern.Body,
        Sources = pattern.Meta.Projects.ToDictionary(
            p => p.Flavor,
            p => Catalog.SourceFiles(repoRoot, p.Path))
    });
});

app.MapGet("/api/source", (string path) =>
{
    var full = Path.GetFullPath(Path.Combine(repoRoot, path));
    if (!full.StartsWith(repoRoot + Path.DirectorySeparatorChar) || !File.Exists(full))
        return Results.NotFound();
    if (Path.GetExtension(full) is not (".cs" or ".csproj" or ".json"))
        return Results.BadRequest("Only source files can be viewed.");

    return Results.Text(File.ReadAllText(full), "text/plain");
});

app.MapGet("/api/run", async (HttpContext context, string id, string flavor) =>
{
    var project = Catalog.Load(patternsDir).FirstOrDefault(p => p.Id == id)?
        .Meta.Projects.FirstOrDefault(p => p.Flavor == flavor);
    if (project is null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    RunSession session;
    try
    {
        session = RunSession.Start(repoRoot, project);
    }
    catch (InvalidOperationException ex)
    {
        context.Response.StatusCode = 429;
        await context.Response.WriteAsync(ex.Message);
        return;
    }

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    try
    {
        // The id/token pair the client needs to reach /api/runs/{id}/input and /cancel - sent as
        // the first event so a single GET both starts the run and hands out its credentials.
        await context.Response.WriteAsync(
            $"event: session\ndata: {JsonSerializer.Serialize(new { session.Id, session.Token }, JsonSerializerOptions.Web)}\n\n");
        await context.Response.Body.FlushAsync();

        await foreach (var chunk in session.Reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk, JsonSerializerOptions.Web)}\n\n");
            await context.Response.Body.FlushAsync();
        }

        // Without an explicit end the browser's EventSource would reconnect and start the sample again.
        await context.Response.WriteAsync("event: end\ndata: {}\n\n");
        await context.Response.Body.FlushAsync();
    }
    finally
    {
        session.Cancel();
    }
});

app.MapPost("/api/runs/{id}/input", async (HttpContext context, string id) =>
{
    var session = RunSession.TryGet(id, context.Request.Headers["X-Run-Token"].ToString());
    if (session is null) return Results.NotFound();

    using var reader = new StreamReader(context.Request.Body);
    session.SendInput((await reader.ReadToEndAsync()).TrimEnd('\n'));
    return Results.Ok();
});

app.MapPost("/api/runs/{id}/cancel", (string id, HttpContext context) =>
{
    var session = RunSession.TryGet(id, context.Request.Headers["X-Run-Token"].ToString());
    if (session is null) return Results.NotFound();

    session.Cancel();
    return Results.Ok();
});

app.Run();
