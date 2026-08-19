using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

// Skill learning: reflect over a trajectory and distill it into a reusable skill FILE.
// Episode 1 solves an unfamiliar task by trial and error (the provisioning system has
// undocumented formats that only surface as tool errors). A reflection pass then writes
// skills/<name>/SKILL.md — YAML frontmatter (name + description) over a step-by-step
// procedure. Episode 2 gets a FRESH agent that sees only the frontmatter index in its
// instructions plus a read_skill tool (progressive disclosure, see
// ProgressiveToolDisclosure) — it loads the skill and provisions without a single error.
// LearningAndAdaptation learns rules into its own prompt; here the learning is a file
// a future session (or a different agent) can pick up.

var skillsDir = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "skill-learning", Guid.NewGuid().ToString("N"))).FullName;

// ---- Episode 1: trial and error against an unfamiliar system ----

Console.WriteLine("---- Episode 1: no skill, trial and error ----\n");

var system1 = new ProvisioningSystem();
var episode1Agent = new ChatClientAgent(Settings.ChatClient,
    "You provision employees in an IT system you have not used before. Tool calls may fail " +
    "with error messages — read them, adapt, and retry until the employee is fully " +
    "provisioned (account, license, team, onboarding meeting). Then summarize what you did.",
    tools: system1.Tools);

var episode1 = await episode1Agent.RunAsync(
    "Fully provision our new employee Maria Fernandez (marketing).");
Console.WriteLine($"Agent: {episode1.Text}\n");
PrintStats("Episode 1", episode1);

// ---- Reflection: distill the trajectory into a skill file ----

var trajectory = string.Join("\n", episode1.Messages.SelectMany(m => m.Contents).Select(c => c switch
{
    FunctionCallContent call => $"CALL {call.Name}({string.Join(", ", call.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])})",
    FunctionResultContent result => $"  -> {result.Result}",
    _ => null
}).Where(line => line is not null));

var skillMarkdown = (await Settings.ChatClient.GetResponseAsync(
    $"""
    Below is the tool-call trajectory of an agent that provisioned an employee by trial
    and error. Distill it into a reusable skill file. Output ONLY the file content:
    YAML frontmatter between --- fences with exactly two fields, "name: provision-employee"
    and a one-line "description", followed by a markdown body with the numbered procedure.
    Capture every exact format and value the errors revealed — those are the hard-won facts.

    {trajectory}
    """)).Text.Trim();

var skillPath = Path.Combine(Directory.CreateDirectory(Path.Combine(skillsDir, "provision-employee")).FullName, "SKILL.md");
File.WriteAllText(skillPath, skillMarkdown);
Console.WriteLine($"\n---- Distilled skill ({skillPath}) ----\n{skillMarkdown}\n");

// ---- Episode 2: fresh agent, fresh system — only the skill index is disclosed ----

Console.WriteLine("---- Episode 2: fresh agent with the skill index ----\n");

// Just the frontmatter description goes into the instructions; the body stays on disk
// until the agent decides it is relevant.
var description = skillMarkdown.Split('\n')
    .FirstOrDefault(l => l.StartsWith("description:"))?["description:".Length..].Trim() ?? "(no description)";

var system2 = new ProvisioningSystem();
var episode2Agent = new ChatClientAgent(Settings.ChatClient,
    $"""
    You provision employees in the company IT system. Learned skills are available:
      - provision-employee: {description}
    Read a skill with read_skill(name) BEFORE attempting a task it covers, and follow
    its procedure exactly. Summarize what you did when finished.
    """,
    tools:
    [
        .. system2.Tools,
        AIFunctionFactory.Create((string name) =>
        {
            var path = Path.Combine(skillsDir, Path.GetFileName(name), "SKILL.md");
            return File.Exists(path) ? File.ReadAllText(path) : $"No such skill: {name}";
        }, "read_skill", "Load the full SKILL.md for a learned skill by name.")
    ]);

var episode2 = await episode2Agent.RunAsync(
    "Fully provision our new employee Jonas Berg (engineering).");
Console.WriteLine($"Agent: {episode2.Text}\n");
PrintStats("Episode 2", episode2);
return;

static void PrintStats(string label, AgentResponse response)
{
    var contents = response.Messages.SelectMany(m => m.Contents).ToList();
    var calls = contents.OfType<FunctionCallContent>().Count();
    var errors = contents.OfType<FunctionResultContent>().Count(r => $"{r.Result}".StartsWith("ERROR"));
    Console.WriteLine($"  [{label}: {calls} tool calls, {errors} failed]");
}

/// <summary>
/// A provisioning backend with undocumented conventions: usernames must be first.last
/// lowercase, the only valid license tier is E5, and team names are internal ids like
/// team-engineering-eu. The tool descriptions do not reveal any of this — errors do.
/// </summary>
internal sealed class ProvisioningSystem
{
    private readonly HashSet<string> _accounts = [];
    private readonly HashSet<string> _licensed = [];
    private readonly HashSet<string> _teamed = [];

    public AITool[] Tools =>
    [
        AIFunctionFactory.Create(CreateAccount), AIFunctionFactory.Create(AssignLicense),
        AIFunctionFactory.Create(AddToTeam), AIFunctionFactory.Create(ScheduleOnboarding)
    ];

    [Description("Create a user account for a new employee.")]
    public string CreateAccount(string username)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-z]+\\.[a-z]+$"))
            return "ERROR: invalid username. Accounts use the form first.last, all lowercase (e.g. jane.doe).";
        _accounts.Add(username);
        return $"OK: account {username} created.";
    }

    [Description("Assign a product license to a user account.")]
    public string AssignLicense(string username, string licenseTier)
    {
        if (!_accounts.Contains(username)) return $"ERROR: no account {username}. Create the account first.";
        if (licenseTier != "E5") return $"ERROR: unknown tier '{licenseTier}'. This tenant only provisions tier E5.";
        _licensed.Add(username);
        return $"OK: E5 license assigned to {username}.";
    }

    [Description("Add a user to a team.")]
    public string AddToTeam(string username, string team)
    {
        if (!_licensed.Contains(username)) return $"ERROR: {username} has no license. A license is required before team membership.";
        if (!team.StartsWith("team-") || !team.EndsWith("-eu"))
            return $"ERROR: unknown team '{team}'. Teams use internal ids of the form team-<department>-eu (e.g. team-marketing-eu).";
        _teamed.Add(username);
        return $"OK: {username} added to {team}.";
    }

    [Description("Schedule the onboarding meeting for a new employee.")]
    public string ScheduleOnboarding(string username)
    {
        if (!_teamed.Contains(username)) return $"ERROR: {username} is not in a team yet. Onboarding is scheduled by the team.";
        return $"OK: onboarding meeting scheduled for {username}.";
    }
}
