using System.Text;

namespace Shared.Sandbox;

public static class BoundedReader
{
    /// <summary>
    /// Reads a stream keeping at most <paramref name="maxCharacters"/> characters, but keeps
    /// DRAINING to end-of-stream — a generated program that prints indefinitely must not be
    /// able to grow host memory, and an undrained pipe would deadlock the child on a full buffer.
    /// </summary>
    public static async Task<string> ReadBoundedAsync(
        TextReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        var retained = new StringBuilder();
        var truncated = false;
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var remaining = maxCharacters - retained.Length;
            if (remaining > 0)
                retained.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining)
                truncated = true;
        }
        if (truncated) retained.Append("\n[output truncated]");
        return retained.ToString();
    }
}
