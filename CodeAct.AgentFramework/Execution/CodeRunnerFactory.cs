namespace CodeAct.AgentFramework.Execution;

/// <summary>
/// Selects the runner. The container sandbox is the default; host execution requires a
/// DOUBLE opt-in (CLI flag AND acknowledgement environment variable) so nobody lands in
/// unsafe mode just because Docker happened to be stopped. Selection NEVER silently
/// falls back to host execution — it fails closed.
/// </summary>
public static class CodeRunnerFactory
{
    public const string UnsafeAcknowledgementVariable = "AGENTIC_PATTERNS_ACKNOWLEDGE_UNSAFE_CODE_EXECUTION";
    public const string UnsafeAcknowledgementValue = "I_UNDERSTAND_THIS_RUNS_UNTRUSTED_CODE_ON_MY_HOST";

    public static IGeneratedCodeRunner Create(CodeExecutionOptions options) =>
        Create(options, ContainerCodeRunner.IsAvailable(options.ContainerRuntime));

    // Container availability is a parameter so tests can verify the selection logic
    // without a container runtime installed.
    public static IGeneratedCodeRunner Create(CodeExecutionOptions options, bool containerRuntimeAvailable)
    {
        if (containerRuntimeAvailable)
            return new ContainerCodeRunner(options);

        if (options.AllowUnsafeHostExecution &&
            Environment.GetEnvironmentVariable(UnsafeAcknowledgementVariable) == UnsafeAcknowledgementValue)
        {
            PrintUnsafeExecutionWarning();
#pragma warning disable CS0618 // the [Obsolete] marker exists to deter every OTHER call site
            return new UnsafeHostCodeRunner(options);
#pragma warning restore CS0618
        }

        throw new InvalidOperationException(
            $"""
            Model-generated code execution was blocked.

            Docker or Podman is required for the default teaching sandbox.

            The sample intentionally does not fall back to host execution because
            generated code may read files, access credentials, use the network,
            start processes, or damage the host.

            Unsafe local execution requires both:
              1. --allow-unsafe-host-execution
              2. {UnsafeAcknowledgementVariable}=
                 {UnsafeAcknowledgementValue}
            """);
    }

    private static void PrintUnsafeExecutionWarning() =>
        Console.Error.WriteLine(
            """
            ================================================================
            DANGER: UNSAFE HOST EXECUTION IS ENABLED

            MODEL-GENERATED CODE WILL RUN DIRECTLY ON THIS MACHINE.

            It may read files, steal credentials, access internal services,
            modify data, start processes, or persist beyond this application.

            This mode exists only to demonstrate the CodeAct pattern when a
            container runtime is unavailable.

            DO NOT USE THIS EXECUTION MODE IN PRODUCTION.
            ================================================================
            """);
}
