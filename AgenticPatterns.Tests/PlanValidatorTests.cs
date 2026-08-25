using Planning.AgentFramework;
using Xunit;

namespace AgenticPatterns.Tests;

public class PlanValidatorTests
{
    static readonly HashSet<string> Allowed =
        new(["GetFlights", "SelectCheapest", "RequestBookingApproval", "BookFlight", "DraftEmail"],
            StringComparer.OrdinalIgnoreCase);

    static Plan PlanOf(params PlanStep[] steps) => new() { Steps = [.. steps] };
    static PlanStep Step(int id, string tool, params (string Name, string Value)[] args) =>
        new() { Id = id, Tool = tool, Args = args.ToDictionary(a => a.Name, a => a.Value) };

    // GetFlights' full, closed parameter set - needed wherever a test wants GetFlights itself to
    // validate cleanly, now that PlanValidator rejects missing/extra argument keys.
    static PlanStep GetFlightsStep(int id) =>
        Step(id, "GetFlights", ("from", "BER"), ("to", "AMS"), ("date", "2026-09-24"));

    [Fact]
    public void UnknownToolIsRejected() =>
        Assert.Contains(PlanValidator.Validate(PlanOf(Step(1, "DropDatabase")), Allowed, 5),
            e => e.Message.Contains("not allowed"));

    [Fact]
    public void DuplicateStepIdsAreRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(Step(1, "GetFlights"), Step(1, "DraftEmail")), Allowed, 5));

    [Fact]
    public void ForwardReferencesAreRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(Step(1, "DraftEmail", ("confirmation", "{{step2}}")), Step(2, "GetFlights")), Allowed, 5));

    [Fact]
    public void ReferenceToAMissingStepIsRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(Step(1, "DraftEmail", ("confirmation", "{{step9}}"))), Allowed, 5));

    [Fact]
    public void TooManyStepsAreRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(Step(1, "GetFlights"), Step(2, "GetFlights"), Step(3, "GetFlights")), Allowed, maxSteps: 2));

    [Fact]
    public void AnEmptyPlanIsRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(PlanOf(), Allowed, 5));

    [Fact]
    public void AWellFormedPlanPasses() =>
        Assert.Empty(PlanValidator.Validate(
            PlanOf(GetFlightsStep(1), Step(2, "SelectCheapest", ("flights", "{{step1}}"))), Allowed, 5));

    [Fact]
    public void UnresolvedPlaceholdersFailInsteadOfBeingPassedThrough() =>
        Assert.Throws<InvalidOperationException>(() => PlanValidator.Resolve(
            new Dictionary<string, string> { ["confirmation"] = "{{step7}}" },
            new Dictionary<string, string>()));

    [Fact]
    public void ResolvedPlaceholdersAreSubstituted() =>
        Assert.Equal("ref ABC123", PlanValidator.Resolve(
            new Dictionary<string, string> { ["confirmation"] = "ref {{step1}}" },
            new Dictionary<string, string> { ["1"] = "ABC123" })["confirmation"]);

    [Fact]
    public void SelfReferencingStepIsRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(Step(1, "DraftEmail", ("confirmation", "{{step1}}"))), Allowed, 5));

    [Fact]
    public void BookFlightSkippingApprovalIsRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(
                Step(1, "GetFlights"),
                Step(2, "SelectCheapest", ("flights", "{{step1}}")),
                Step(3, "BookFlight", ("approvedFlight", "{{step2}}"))), // skips RequestBookingApproval
            Allowed, 5));

    [Fact]
    public void BookFlightWithAFabricatedLiteralIsRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(
                Step(1, "GetFlights"),
                Step(2, "SelectCheapest", ("flights", "{{step1}}")),
                Step(3, "RequestBookingApproval", ("flight", "{{step2}}")),
                Step(4, "BookFlight",
                    ("approvedFlight", """{"FlightId":"F999","Departs":"00:00","PriceEur":1.00}"""))),
            Allowed, 5));

    [Fact]
    public void AFullyProvenancedPlanPasses() =>
        Assert.Empty(PlanValidator.Validate(
            PlanOf(
                GetFlightsStep(1),
                Step(2, "SelectCheapest", ("flights", "{{step1}}")),
                Step(3, "RequestBookingApproval", ("flight", "{{step2}}")),
                Step(4, "BookFlight", ("approvedFlight", "{{step3}}")),
                Step(5, "DraftEmail", ("confirmation", "{{step4}}"))),
            Allowed, 5));

    [Fact]
    public void DecoyArgumentKeyDoesNotBypassProvenance() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(
                GetFlightsStep(1),
                Step(2, "SelectCheapest", ("flights", "{{step1}}")),
                Step(3, "RequestBookingApproval", ("flight", "{{step2}}")),
                Step(4, "BookFlight",
                    ("approvedFlight", """{"FlightId":"F999","Departs":"00:00","PriceEur":1.00}"""),
                    ("decoy", "{{step3}}")), // the real parameter is fabricated; decoy carries the valid reference
                Step(5, "DraftEmail", ("confirmation", "{{step4}}"))),
            Allowed, 5));

    [Fact]
    public void StepMissingARequiredParameterIsRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(Step(1, "GetFlights", ("from", "BER"))), // missing "to" and "date"
            Allowed, 5));

    [Fact]
    public void DraftEmailWithAFabricatedLiteralIsRejected() =>
        Assert.NotEmpty(PlanValidator.Validate(
            PlanOf(
                GetFlightsStep(1),
                Step(2, "SelectCheapest", ("flights", "{{step1}}")),
                Step(3, "RequestBookingApproval", ("flight", "{{step2}}")),
                Step(4, "BookFlight", ("approvedFlight", "{{step3}}")),
                Step(5, "DraftEmail", ("confirmation", "Subject: fake\n\nBooked!"))), // not {{step4}}
            Allowed, 5));
}
