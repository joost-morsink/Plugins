namespace BitWizards.Plugins.Installation;

public static class PluginCleaner
{
    /// <summary>
    /// Empties the plugin directory (the directory itself is kept).
    /// Refuses when the directory is, or contains, a path that must not be wiped,
    /// such as the tool's own directory, the working directory or the user's home directory.
    /// </summary>
    /// <returns>The number of top-level entries removed.</returns>
    public static int Clean(string pluginDirectory, IEnumerable<string> protectedPaths, TextWriter log)
    {
        var directory = Normalize(pluginDirectory);
        if (directory == Normalize(Path.GetPathRoot(directory) ?? directory))
            throw new InvalidOperationException($"Refusing to clean '{directory}': it is a root directory.");
        foreach (var protectedPath in protectedPaths.Where(p => !string.IsNullOrEmpty(p)).Select(Normalize))
            if (IsSameOrAncestor(directory, protectedPath))
                throw new InvalidOperationException(
                    $"Refusing to clean '{directory}': it contains '{protectedPath}'.");

        if (!Directory.Exists(directory))
            return 0;

        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry) && !new DirectoryInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(entry, true);
            else
                File.Delete(entry);
            log.WriteLine($"Removed {Path.GetFileName(entry)}");
            removed++;
        }
        return removed;
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrAncestor(string candidate, string other)
        => other.Equals(candidate, StringComparison.OrdinalIgnoreCase)
           || other.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
