namespace CodeAct.AgentFramework.Execution;

/// <summary>
/// Executes model-generated code. Model-generated code is UNTRUSTED code: implementations
/// must isolate it from the host (see <see cref="ContainerCodeRunner"/>) or make the lack
/// of isolation impossible to enable by accident (see <see cref="UnsafeHostCodeRunner"/>).
/// </summary>
public interface IGeneratedCodeRunner
{
    Task<ExecutionResult> RunAsync(string sourceCode, CancellationToken cancellationToken);
}
