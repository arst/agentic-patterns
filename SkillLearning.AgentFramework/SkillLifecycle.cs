using System.Security.Cryptography;
using System.Text.Json;

namespace SkillLearning.AgentFramework;

public enum SkillStage { Candidate, Validated, Tested, Approved, Active, Retired }

public sealed record SkillManifest(
    string Name,
    int Version,
    SkillStage Stage,
    DateTimeOffset CreatedAt,
    string ContentSha256,
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
            DateTimeOffset.UtcNow, ContentSha256: "");
        Directory.CreateDirectory(VersionDirectory(manifest));
        File.WriteAllText(SkillPath(manifest), markdown);
        manifest = manifest with { ContentSha256 = Digest(SkillPath(manifest)) };
        Save(manifest);
        return manifest;
    }

    public SkillManifest Validate(string name)
    {
        var manifest = Require(name, SkillStage.Candidate);
        var markdown = ReadVerified(manifest);
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
        if (!test(ReadVerified(manifest)))
            throw new InvalidDataException("Skill contract tests failed; candidate was not promoted.");
        return Transition(manifest, SkillStage.Tested);
    }

    public SkillManifest Approve(string name, string reviewer)
    {
        var manifest = Require(name, SkillStage.Tested);
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A trusted reviewer is required.");
        ReadVerified(manifest);
        return Transition(manifest with { ApprovedBy = reviewer }, SkillStage.Approved);
    }

    public SkillManifest Activate(string name)
    {
        var manifest = Require(name, SkillStage.Approved);
        ReadVerified(manifest);
        return Transition(manifest, SkillStage.Active);
    }

    public SkillManifest Retire(string name)
    {
        var manifest = Require(name, SkillStage.Active);
        ReadVerified(manifest);
        return Transition(manifest, SkillStage.Retired);
    }

    public string? ReadActive(string name)
    {
        var manifest = Load(SafeName(name));
        return manifest?.Stage == SkillStage.Active ? ReadVerified(manifest) : null;
    }

    public SkillManifest? Load(string name)
    {
        var path = ManifestPath(SafeName(name));
        // ponytail: a manifest.json written before ContentSha256 existed deserializes with a null
        // digest and then fails ReadVerified with the tamper message, not a migration message. No
        // migration path for a sample; add one if this ever needs to read pre-existing manifests.
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

    private static string Digest(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Re-verifies the content against the digest recorded at candidate creation. Called on every
    /// transition and every read, so an approved skill cannot be swapped out underneath an
    /// already-granted approval. A version directory is immutable by policy once the candidate is
    /// created (nothing here enforces that on disk); the only legal way to change a skill is a new
    /// version.
    ///
    /// Know exactly what this buys, because the two are routinely confused:
    /// <list type="bullet">
    /// <item>A SHA-256 in the manifest <b>detects unexpected content mutation</b> — a partial
    /// write, a stray editor save, a sync tool, a bug elsewhere in this process, anything that
    /// changed SKILL.md without going through a new version.</item>
    /// <item>It <b>does not authenticate the content against an attacker</b>. manifest.json sits
    /// in the same directory as the file it vouches for, so whoever can write one can write the
    /// other and recompute the digest to match. The digest is a checksum, not a signature: it has
    /// no secret and no external root of trust, so it cannot survive an adversary who already has
    /// the write access it is checking.</item>
    /// </list>
    /// Closing that second gap is a different mechanism, not a stronger hash: sign the approved
    /// manifest with a key the agent cannot reach, or keep the manifest store outside the agent's
    /// write scope entirely (a registry it can read and a reviewer can write). The filesystem is
    /// the trust boundary this sample stops at, deliberately — see PatternExplorer/patterns/
    /// SkillLearning.md.
    /// </summary>
    private string ReadVerified(SkillManifest manifest)
    {
        var path = SkillPath(manifest);
        if (Digest(path) != manifest.ContentSha256)
            throw new InvalidDataException(
                $"Skill '{manifest.Name}' v{manifest.Version} no longer matches the digest recorded " +
                "at candidate creation; refusing to load it.");
        return File.ReadAllText(path);
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

/// <summary>
/// The contract test a candidate must pass before a reviewer is asked to look at it. It checks
/// two things: that the procedure records the four calls in the order the system enforces, and
/// that it carries the conventions episode 1 could only have learned from error messages.
///
/// It deliberately does NOT assert the username format. That rule exists in
/// <c>ProvisioningSystem.CreateAccount</c>, but an agent asked to provision "Maria Fernandez"
/// guesses <c>maria.fernandez</c> on its first try and the regex accepts it — so the rule never
/// produces an error, never enters the trajectory, and cannot appear in a distilled skill.
/// Asserting a fact the episode never teaches makes the gate reject every correct skill, which is
/// exactly what it used to do. A contract test may only assert what the run can actually produce.
///
/// ponytail: a substring-order check over model prose. It confirms the four calls and the two
/// learned constants are written down in the right order, NOT that the skill works. The real
/// version runs the distilled procedure against a fresh ProvisioningSystem and asserts the
/// employee ends up provisioned; that needs a model call per promotion, so it is out of scope for
/// a sample. See PatternExplorer/patterns/SkillLearning.md.
/// </summary>
public static class ProvisionEmployeeSkillTests
{
    // Tool names, not prose: these appear verbatim in the trajectory, so the reflection echoes
    // them reliably, whereas a template like "first.last" only survives if an error quoted it.
    private static readonly string[] Procedure =
        ["CreateAccount", "AssignLicense", "AddToTeam", "ScheduleOnboarding"];

    public static bool Pass(string markdown)
    {
        // Strictly increasing first occurrences: a missing step is IndexOf -1 and fails here too.
        var previous = -1;
        foreach (var step in Procedure)
        {
            var position = markdown.IndexOf(step, StringComparison.OrdinalIgnoreCase);
            if (position <= previous) return false;
            previous = position;
        }

        // The two facts the tool descriptions never state. A candidate that merely restates the
        // tool list has learned nothing from episode 1 and must not reach a reviewer.
        return markdown.Contains("E5", StringComparison.Ordinal) &&
               markdown.Contains("team-", StringComparison.OrdinalIgnoreCase);
    }
}
