namespace PatternExplorer;

// Pulled out of Program.cs so a test can assert the header value without a WebApplicationFactory
// (not available in this environment - see AgenticPatterns.Tests/SecurityHeadersTests.cs).
internal static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'self'; base-uri 'self'; form-action 'self'; " +
        "frame-ancestors 'none'";
}
