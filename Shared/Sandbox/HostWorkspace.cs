namespace Shared.Sandbox;

/// <summary>
/// Host-side staging for the directory a sandbox bind-mounts. The container runs as an
/// unrelated uid (65532 by default), so every directory it traverses and every file it reads
/// has to be world-readable — and the mode argument on <c>Directory.CreateDirectory</c> is a
/// <c>mkdir(2)</c> mode, itself masked by the process umask (verified by hand: umask 077 turns
/// a requested 0755 into 0700). <c>chmod(2)</c> is not subject to umask, so the explicit
/// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> afterwards is what actually forces
/// the bits. This lived twice — once in CodeAct's ContainerCodeRunner, once in
/// StigmergicCoordination's BuildGate, where the identical gap had to be found independently —
/// so it lives next to the boundary that needs it instead.
/// </summary>
public static class HostWorkspace
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode FileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Creates <paramref name="path"/> and its parent world-traversable, umask or not.</summary>
    public static string CreateWorldReadableDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return Directory.CreateDirectory(path).FullName;

        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, DirectoryMode);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, DirectoryMode);
        return path;
    }

    /// <summary>Writes a file the sandbox uid can read — the directory mode alone is not enough.</summary>
    public static async Task WriteWorldReadableAsync(string path, string content, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, FileMode);
    }
}
