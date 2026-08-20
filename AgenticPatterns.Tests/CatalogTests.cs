using PatternExplorer;
using Xunit;

namespace AgenticPatterns.Tests;

public class CatalogTests
{
    private static Pattern ParseOne(string frontMatterJson)
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "catalog-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Sample.md"), $"---\n{frontMatterJson}\n---\n\nBody.");
            return Catalog.Load(dir).Single();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FrontMatterWithoutRisk_ParsesWithNullRisk()
    {
        // All pre-existing pattern files omit "risk" — they must keep parsing
        var pattern = ParseOne("""
            { "title": "T", "summary": "S", "category": "C",
              "projects": [ { "flavor": "AgentFramework", "path": "X" } ] }
            """);

        Assert.Null(pattern.Meta.Risk);
        Assert.Equal("T", pattern.Meta.Title);
    }

    [Fact]
    public void FrontMatterWithRisk_IsPopulated()
    {
        var pattern = ParseOne("""
            { "title": "T", "summary": "S", "category": "C", "risk": "Runs untrusted code.",
              "projects": [] }
            """);

        Assert.Equal("Runs untrusted code.", pattern.Meta.Risk);
    }
}
