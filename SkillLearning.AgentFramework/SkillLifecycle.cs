using System.Text.Json;

namespace SkillLearning.AgentFramework;

public enum SkillStage { Candidate, Validated, Tested, Approved, Active, Retired }

public sealed record SkillManifest(
    string Name,
    int Version,
    SkillStage Stage,
    DateTimeOffset CreatedAt,
    string? ApprovedBy = null);

public sealed class SkillLifecycle(string skillsDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SkillManifest CreateCandidate(string name, string markdown)
    {
        name = SafeName(name);
        var existing = Load(name);
        if (existing is not null && existing.Stage != SkillStage.Retired)
            throw new InvalidOperationException("The current skill version must be retired before creating another.");
        var manifest = new SkillManifest(name, (existing?.Version ?? 0) + 1, SkillStage.Candidate,
            DateTimeOffset.UtcNow);
        Directory.CreateDirectory(VersionDirectory(manifest));
        File.WriteAllText(SkillPath(manifest), markdown);
        Save(manifest);
        return manifest;
    }

    public SkillManifest Validate(string name)
    {
        var manifest = Require(name, SkillStage.Candidate);
        var markdown = File.ReadAllText(SkillPath(manifest));
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var closingFence = Array.IndexOf(lines, "---", 1);
        var frontmatter = closingFence > 0 ? lines[1..closingFence] : [];
        if (markdown.Length > 20_000 || lines.FirstOrDefault() != "---" || closingFence < 3 ||
            frontmatter.Length != 2 || !frontmatter.Contains($"name: {manifest.Name}") ||
            !frontmatter.Any(line => line.StartsWith("description: ", StringComparison.Ordinal) && line.Length > 13) ||
            !lines.Skip(closingFence + 1).Any(line => !string.IsNullOrWhiteSpace(line)))
            throw new InvalidDataException("Skill must have exact name/description frontmatter and a non-empty body under 20 KB.");
        return Transition(manifest, SkillStage.Validated);
    }

    public SkillManifest MarkTested(string name, Func<string, bool> test)
    {
        var manifest = Require(name, SkillStage.Validated);
        if (!test(File.ReadAllText(SkillPath(manifest))))
            throw new InvalidDataException("Skill contract tests failed; candidate was not promoted.");
        return Transition(manifest, SkillStage.Tested);
    }

    public SkillManifest Approve(string name, string reviewer)
    {
        var manifest = Require(name, SkillStage.Tested);
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A trusted reviewer is required.");
        return Transition(manifest with { ApprovedBy = reviewer }, SkillStage.Approved);
    }

    public SkillManifest Activate(string name) => Transition(Require(name, SkillStage.Approved), SkillStage.Active);

    public SkillManifest Retire(string name) => Transition(Require(name, SkillStage.Active), SkillStage.Retired);

    public string? ReadActive(string name)
    {
        var manifest = Load(SafeName(name));
        return manifest?.Stage == SkillStage.Active ? File.ReadAllText(SkillPath(manifest)) : null;
    }

    public SkillManifest? Load(string name)
    {
        var path = ManifestPath(SafeName(name));
        return File.Exists(path) ? JsonSerializer.Deserialize<SkillManifest>(File.ReadAllText(path), Json) : null;
    }

    private SkillManifest Require(string name, SkillStage stage)
    {
        var manifest = Load(SafeName(name)) ?? throw new InvalidOperationException("Skill does not exist.");
        return manifest.Stage == stage
            ? manifest
            : throw new InvalidOperationException($"Expected {stage}, found {manifest.Stage}.");
    }

    private SkillManifest Transition(SkillManifest manifest, SkillStage stage)
    {
        manifest = manifest with { Stage = stage };
        Save(manifest);
        return manifest;
    }

    private void Save(SkillManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(manifest.Name))!);
        File.WriteAllText(ManifestPath(manifest.Name), JsonSerializer.Serialize(manifest, Json));
    }

    private string VersionDirectory(SkillManifest manifest) =>
        Path.Combine(skillsDirectory, manifest.Name, "versions", manifest.Version.ToString());
    private string SkillPath(SkillManifest manifest) => Path.Combine(VersionDirectory(manifest), "SKILL.md");
    private string ManifestPath(string name) => Path.Combine(skillsDirectory, name, "manifest.json");

    private static string SafeName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && Path.GetFileName(name) == name
            ? name
            : throw new ArgumentException("Skill name must be one safe path segment.");
}

public static class ProvisionEmployeeSkillTests
{
    public static bool Pass(string markdown)
    {
        var account = markdown.IndexOf("first.last", StringComparison.OrdinalIgnoreCase);
        var license = markdown.IndexOf("E5", StringComparison.Ordinal);
        var team = markdown.IndexOf("team-", StringComparison.OrdinalIgnoreCase);
        var onboarding = markdown.IndexOf("onboarding", StringComparison.OrdinalIgnoreCase);
        return account >= 0 && account < license && license < team && onboarding >= 0;
    }
}
