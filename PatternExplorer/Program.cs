using System.Text.Json;
using PatternExplorer;

var repoRoot = Catalog.FindRepoRoot();
var patternsDir = Path.Combine(repoRoot, "PatternExplorer", "patterns");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5080");

var app = builder.Build();
app.UseDefaultFiles();
// Local authoring tool: never cache, so edits to the page or the pattern files show up on refresh.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store"
});

app.MapGet("/api/patterns", () => Catalog.Load(patternsDir)
    .Select(p => new { p.Id, p.Meta.Title, p.Meta.Summary, p.Meta.Category, p.Meta.Projects, p.Meta.Risk }));

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
        pattern.Meta.Projects,
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

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    var session = RunSession.Start(repoRoot, project);
    try
    {
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

app.MapPost("/api/run/input", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    RunSession.Current?.SendInput((await reader.ReadToEndAsync()).TrimEnd('\n'));
    return Results.Ok();
});

app.MapPost("/api/run/cancel", () =>
{
    RunSession.Current?.Cancel();
    return Results.Ok();
});

app.Run();
