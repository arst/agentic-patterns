using PatternExplorer;
using Xunit;

namespace AgenticPatterns.Tests;

/// <summary>
/// This is a content assertion on the <see cref="SecurityHeaders.ContentSecurityPolicy"/>
/// constant, not coverage of the header actually being registered: a WebApplicationFactory-based
/// end-to-end assertion isn't available in this environment (Program.cs's top-level-statement
/// type isn't exposed for one, and the project brings no Mvc.Testing/TestHost package). Deleting
/// the `app.Use` block that writes this header in Program.cs would leave this test green. Manual
/// verification of the live header (curl, and a Playwright-driven page load with zero CSP console
/// violations) is in the task report.
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
        Assert.Contains("base-uri 'self'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("form-action 'self'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'none'", SecurityHeaders.ContentSecurityPolicy);

        // No loosening beyond the brief's starting policy was needed - verified live in the
        // report - so nothing here should grant 'unsafe-eval' or a wildcard source.
        Assert.DoesNotContain("unsafe-eval", SecurityHeaders.ContentSecurityPolicy);
        Assert.DoesNotContain("*", SecurityHeaders.ContentSecurityPolicy);
    }
}

/// M5: `GET /api/run` starts a process and spends money, and the per-run token gates only /input
/// and /cancel - so any page the operator visits could fire one with a bare `<img src=...>`.
/// CSP frame-ancestors/form-action do not stop a cross-origin GET; Sec-Fetch-Site does.
public class CrossSiteRequestTests
{
    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")] // another localhost port is still not this app
    public void Requests_the_browser_marks_as_foreign_are_rejected(string secFetchSite) =>
        Assert.True(SecurityHeaders.IsCrossSiteRequest(secFetchSite));

    [Theory]
    [InlineData("same-origin")] // the page's own EventSource
    [InlineData("none")]        // typed into the address bar
    [InlineData("")]            // curl, or a browser too old to send the header
    [InlineData(null)]
    public void The_apps_own_requests_and_header_less_clients_are_allowed(string? secFetchSite) =>
        Assert.False(SecurityHeaders.IsCrossSiteRequest(secFetchSite));
}
