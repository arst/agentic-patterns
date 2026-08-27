using SkillLearning.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

/// <summary>
/// The skill contract test is the gate between "a model wrote something" and "a reviewer is asked
/// to approve it", so it has to fire on real reflection output, not on a hand-written ideal. The
/// fixture below IS real output from a SkillLearning run — which is how the original check was
/// found to reject every correct skill the sample produced.
/// </summary>
public class ProvisionEmployeeSkillContractTests
{
    /// Real reflection output, trimmed. Note what it does NOT contain: the literal template
    /// "first.last". Episode 1's agent names the account `maria.fernandez` on its first try and
    /// the regex accepts it, so the username rule never becomes an error, never enters the
    /// trajectory, and cannot be distilled. The two rules that DO surface as errors — the E5 tier
    /// and the `team-<department>-eu` id — are captured verbatim, because the error text is what
    /// the reflection has to work from.
    private const string RealDistilledSkill = """
        ---
        name: provision-employee
        description: Procedure to provision an employee account with correct license, team, and onboarding in this tenant.
        ---

        1. Create the employee account
           - Call `CreateAccount` with the desired username.
             - Example: `CreateAccount(username=maria.fernandez)`

        2. Assign the required license **before** any team membership
           - Use `AssignLicense` with `licenseTier` set **exactly** to `E5`.
           - Do **not** use other tiers (e.g. `Standard`), as this tenant only provisions tier `E5`.

        3. Add the employee to a valid internal team
           - Use `AddToTeam` only after the license is assigned.
           - `team` must be an internal id of the form: `team-<department>-eu`.
             - Example: `AddToTeam(username=maria.fernandez, team=team-marketing-eu)`

        4. Schedule onboarding **after** team assignment
           - Use `ScheduleOnboarding` only once the user is in a team.
        """;

    [Fact]
    public void RealReflectionOutputPasses() =>
        Assert.True(ProvisionEmployeeSkillTests.Pass(RealDistilledSkill));

    /// A skill that lists the calls in the wrong order sends the next agent straight back into
    /// the error loop episode 1 just climbed out of — the system refuses a licence before an
    /// account exists and a team before a licence.
    [Fact]
    public void StepsOutOfOrderFail()
    {
        const string outOfOrder = """
            ---
            name: provision-employee
            description: Provision an employee.
            ---
            1. Use `AddToTeam` with an internal id of the form `team-<department>-eu`.
            2. Use `AssignLicense` with `licenseTier` set to `E5`.
            3. Use `CreateAccount` with the username.
            4. Use `ScheduleOnboarding`.
            """;
        Assert.False(ProvisionEmployeeSkillTests.Pass(outOfOrder));
    }

    [Fact]
    public void AMissingStepFails() =>
        Assert.False(ProvisionEmployeeSkillTests.Pass(
            RealDistilledSkill.Replace("ScheduleOnboarding", "(step omitted)")));

    /// The point of the gate: a skill that merely restates the tool list has learned nothing.
    /// Both undocumented conventions were only ever visible in episode 1's error messages.
    [Fact]
    public void RestatingTheToolListWithoutTheErrorTaughtFactsFails()
    {
        const string noFacts = """
            ---
            name: provision-employee
            description: Provision an employee.
            ---
            1. CreateAccount(username)
            2. AssignLicense(username, licenseTier)
            3. AddToTeam(username, team)
            4. ScheduleOnboarding(username)
            """;
        Assert.False(ProvisionEmployeeSkillTests.Pass(noFacts));
    }
}
