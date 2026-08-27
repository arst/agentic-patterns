namespace ToolAuthorization.AgentFramework;

/// <summary>
/// The approval channel: the side of the boundary a real host has to build. One interface, one
/// implementation, on purpose — the only implementation in this repo is a FAKE, and naming the
/// seam is what tells a reader which half is missing. Approval never travels on the
/// model-controlled channel: an approver reads the <see cref="PendingApproval"/> snapshot the
/// host judged and answers out of band.
/// </summary>
public interface IApprover
{
    Task<bool> ApproveAsync(PendingApproval pending, CancellationToken cancellationToken = default);
}

/// <summary>
/// Answers the same way every time so the sample runs with no TTY and no credentials — and says
/// so on the console each time it is asked. This exists as a named type rather than a
/// <c>var approverApproved = true;</c> because that line, copied into a real host, is a silent
/// auto-approver that no reviewer notices; a call to something called <c>DemoApprover</c> is not.
/// ponytail: a constant answer. Upgrade path: await a durable approval record (see
/// DurableHumanInTheLoop) and resume from it — never Console.ReadLine, which hangs an
/// unattended run.
/// </summary>
public sealed class DemoApprover(bool alwaysApprove) : IApprover
{
    public Task<bool> ApproveAsync(PendingApproval pending, CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"  [DEMO APPROVER: automatically {(alwaysApprove ? "approving" : "declining")} " +
            $"{pending.ToolName} — a real host awaits a human on a durable channel]");
        return Task.FromResult(alwaysApprove);
    }
}
