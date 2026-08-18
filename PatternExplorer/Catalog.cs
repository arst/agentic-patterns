using System.Text.Json;

namespace PatternExplorer;

/// <param name="Flavor">"AgentFramework" or "SemanticKernel" - which SDK this project uses.</param>
/// <param name="Path">Project directory, relative to the repo root.</param>
/// <param name="Interactive">The sample reads from stdin, so the UI shows an input box.</param>
/// <param name="Server">Optional companion project to start first (A2A).</param>
/// <param name="ServerPort">Port to poll before starting the main project.</param>
/// <param name="Note">Extra requirement shown next to the run button (e.g. "needs npx").</param>
public record PatternProject(
    string Flavor,
    string Path,
    bool Interactive = false,
    string? Server = null,
    int ServerPort = 0,
    string? Note = null);

public record PatternMeta(string Title, string Summary, string Category, PatternProject[] Projects);

public record Pattern(string Id, PatternMeta Meta, string Body);

public static class Catalog
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// Walks up from the running app until it finds the solution file.
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Agentic Patterns.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// Re-read on every request - the catalog is ~40 small files and live edits beat caching.
    public static List<Pattern> Load(string patternsDir) =>
        [.. Directory.EnumerateFiles(patternsDir, "*.md")
            .Select(Parse)
            .OrderBy(p => p.Meta.Title, StringComparer.OrdinalIgnoreCase)];

    /// Front matter is a JSON object between `---` fences - no YAML parser needed.
    static Pattern Parse(string file)
    {
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        if (!text.StartsWith("---\n"))
            throw new InvalidOperationException($"{file}: missing `---` front matter fence.");

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"{file}: unterminated front matter.");

        var meta = JsonSerializer.Deserialize<PatternMeta>(text[4..end], JsonOptions)
                   ?? throw new InvalidOperationException($"{file}: front matter is not a JSON object.");

        return new Pattern(System.IO.Path.GetFileNameWithoutExtension(file), meta, text[(end + 4)..].TrimStart());
    }

    /// Source files of a sample project, repo-relative, for the source viewer.
    public static List<string> SourceFiles(string repoRoot, string projectPath)
    {
        var dir = System.IO.Path.Combine(repoRoot, projectPath);
        if (!Directory.Exists(dir)) return [];

        return [.. Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".csproj"))
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}")
                        && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"))
            .Select(f => System.IO.Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))
            .OrderBy(f => f.EndsWith(".csproj"))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }
}
