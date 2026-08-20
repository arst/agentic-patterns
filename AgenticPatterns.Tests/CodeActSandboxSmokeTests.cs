using CodeAct.AgentFramework.Execution;
using Xunit;

namespace AgenticPatterns.Tests;

// Live verification of the sandbox BOUNDARY, not just the arguments that request it:
// runs a probe script through the real ContainerCodeRunner and asserts what the code
// inside actually sees. Needs Docker (present on GitHub's ubuntu runners); on machines
// without it the tests pass vacuously rather than fail the suite.
public class CodeActSandboxSmokeTests
{
    private static readonly bool DockerAvailable = ContainerCodeRunner.IsAvailable("docker");

    [Fact]
    public async Task SandboxedCodeHasNoNetworkNoRootAndNoWritableFilesystem()
    {
        if (!DockerAvailable) return;

        var runner = new ContainerCodeRunner(new CodeExecutionOptions());
        var result = await runner.RunAsync(
            """
            using System.Net.NetworkInformation;
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Count(i => i.OperationalStatus == OperationalStatus.Up
                         && i.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            Console.WriteLine($"interfaces-up:{up}");
            Console.WriteLine($"uid-is-root:{Environment.UserName == "root"}");
            try { File.WriteAllText("/workspace/escape.txt", "x"); Console.WriteLine("workspace:writable"); }
            catch { Console.WriteLine("workspace:readonly"); }
            try { File.WriteAllText("/etc/escape.txt", "x"); Console.WriteLine("rootfs:writable"); }
            catch { Console.WriteLine("rootfs:readonly"); }
            """,
            CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("interfaces-up:0", result.StandardOutput);
        Assert.Contains("uid-is-root:False", result.StandardOutput);
        Assert.Contains("workspace:readonly", result.StandardOutput);
        Assert.Contains("rootfs:readonly", result.StandardOutput);
    }

    [Fact]
    public async Task FailedScriptsSurfaceCompilerErrorsWithoutTimingOut()
    {
        if (!DockerAvailable) return;

        var runner = new ContainerCodeRunner(new CodeExecutionOptions());
        var result = await runner.RunAsync("this is not C#;", CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
    }
}
