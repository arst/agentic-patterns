namespace PatternExplorer;

// Pulled out of Program.cs so a test can assert the header value without a WebApplicationFactory
// (not available in this environment - see AgenticPatterns.Tests/SecurityHeadersTests.cs).
internal static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'self'; base-uri 'self'; form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>
    /// True when the browser told us the request came from another site. `GET /api/run` starts a
    /// child process and burns billed model calls, so an `&lt;img src="http://localhost:5080/api/run?
    /// id=CodeAct&amp;flavor=AgentFramework"&gt;` on any page the user happens to visit must not be
    /// able to trigger one - and CSP `frame-ancestors`/`form-action` do not stop a cross-origin GET.
    /// The page's own EventSource sends `same-origin`; a typed URL sends `none`.
    /// ponytail: a header check is the whole CSRF defence. Clients that send no Sec-Fetch-Site at
    /// all (curl, pre-2020 browsers) are let through, which is fine for a single-user localhost
    /// authoring tool and costs one `if`. Upgrade path: require the same per-run token `/input` and
    /// `/cancel` already use - i.e. a real double-submit - if Explorer is ever exposed beyond
    /// localhost.
    /// </summary>
    public static bool IsCrossSiteRequest(string? secFetchSite) =>
        !string.IsNullOrEmpty(secFetchSite) && secFetchSite is not ("same-origin" or "none");
}
