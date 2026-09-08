namespace Curia.Client;

/// <summary>
/// Writing files only their owner can read, and the modes that means.
///
/// <para>Extracted so the two things this client keeps on disk -- an agent's keys
/// (<see cref="ProfileStore"/>) and the signed tree head it retains (<see cref="HeadStore"/>) --
/// share one implementation. R6.53 requires the retained head sit "at the same private file mode as
/// an agent's keys", and the way to keep two things at the same mode is for there to be one mode.</para>
/// </summary>
internal static class PrivateFiles
{
    internal const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, DirectoryMode);
    }

    /// <summary>
    /// Writes privately and atomically: a fresh sibling file created at
    /// <see cref="Mode"/>, then renamed over the destination.
    ///
    /// <para><b>The mode is applied before any content reaches the file.</b> Creating the file and
    /// chmod-ing it afterwards leaves a window in which the contents exist at the prevailing umask,
    /// which on a shared machine is the whole of the exposure.</para>
    ///
    /// <para><b>Via a temp file, because <c>UnixCreateMode</c> only applies on creation.</b> Opening
    /// an existing path with <c>FileMode.Create</c> truncates and rewrites it and leaves its mode
    /// exactly as it was — so a head file that had once been made group-readable stayed that way
    /// through every subsequent write, and R6.53's "at the same private file mode as an agent's
    /// keys" was met on the first write and missed on every one after. Writing a new file and
    /// renaming it means the mode is always the mode of something this method created. Measured, not
    /// assumed: a standalone program reproducing the old call rewrote a 0666 file and left it
    /// 0666.</para>
    ///
    /// <para><b>And atomically, because the previous shape could throw.</b> <c>FileShare.None</c> is
    /// enforced with <c>flock</c> on macOS across processes <i>and</i> across open handles in one
    /// process, so two concurrent <c>curia verify</c> runs sharing one retained head raced: 7,619 of
    /// 20,000 contended writes failed with <c>IOException</c>, out through a call site with no catch
    /// anywhere above it. A rename never partially writes and never contends, so a reader sees the
    /// old file or the new one and never a truncated one.</para>
    /// </summary>
    internal static void Write(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        var temporary = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        var options = new FileStreamOptions
        {
            // CreateNew, not Create: the point is that this method created what it chmods, and a
            // name that already exists means something else is using it.
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = Mode;

        try
        {
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // A failed write must not leave a dot-file behind that the next run trips over. The
            // destination is untouched either way, which is the property that matters.
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Why a path is readable beyond its owner, or null when it is not. Checked on <i>read</i> and
    /// not merely set on write: setting <c>0600</c> at creation protects a file this client wrote
    /// and says nothing about one restored from a backup, copied between machines, or created before
    /// a umask was fixed.
    /// </summary>
    internal static string? ReadableBeyondOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return null;

        const UnixFileMode others =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        var mode = File.GetUnixFileMode(path);
        return (mode & others) == 0 ? null : $"{path} is readable beyond its owner ({mode})";
    }
}
