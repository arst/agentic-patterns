using PatternExplorer;
using Xunit;

namespace AgenticPatterns.Tests;

/// <summary>
/// A WebApplicationFactory-based end-to-end header assertion isn't available in this environment
/// (Program.cs's top-level-statement type isn't exposed for one, and the project brings no
/// Mvc.Testing/TestHost package), so this asserts the constant the middleware in Program.cs
/// actually writes into the response, rather than an HTTP round trip. Manual verification of the
/// live header (curl, and a Playwright-driven page load with zero CSP console violations) is in
/// the task report.
/// </summary>
public class SecurityHeadersTests
{
    [Fact]
    public void ContentSecurityPolicy_restricts_to_self_and_permits_inline_style()
    {
        Assert.Contains("default-src 'self'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("script-src 'self'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("style-src 'self' 'unsafe-inline'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("img-src 'self' data:", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("connect-src 'self'", SecurityHeaders.ContentSecurityPolicy);

        // No loosening beyond the brief's starting policy was needed - verified live in the
        // report - so nothing here should grant 'unsafe-eval' or a wildcard source.
        Assert.DoesNotContain("unsafe-eval", SecurityHeaders.ContentSecurityPolicy);
        Assert.DoesNotContain("*", SecurityHeaders.ContentSecurityPolicy);
    }
}
