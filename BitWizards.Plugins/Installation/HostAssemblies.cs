using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using NuGet.Versioning;

namespace BitWizards.Plugins.Installation;

/// <summary>
/// What the running tool already provides, so that plugin installation does not download it again:
/// assemblies by simple name (the framework and the tool's own directory, from the runtime's trusted platform
/// assemblies) and, when the tool's <c>deps.json</c> is available, packages by id and version.
/// </summary>
public sealed class HostAssemblies
{
    private readonly Dictionary<string, Func<Version?>> _assemblies;
    private readonly Dictionary<string, NuGetVersion> _packages;

    private HostAssemblies(
        Dictionary<string, Func<Version?>> assemblies,
        Dictionary<string, NuGetVersion> packages,
        string description)
    {
        _assemblies = assemblies;
        _packages = packages;
        Description = description;
    }

    /// <summary>
    /// Describes where the host's package list came from, for reporting.
    /// </summary>
    public string Description { get; }

    public bool TryGetAssembly(string simpleName, out Version? version)
    {
        if (_assemblies.TryGetValue(simpleName, out var lazy))
        {
            version = lazy();
            return true;
        }
        version = null;
        return false;
    }

    public bool TryGetPackage(string id, out NuGetVersion version)
        => _packages.TryGetValue(id, out version!);

    /// <summary>
    /// For tests and callers that know exactly what the host provides.
    /// </summary>
    public static HostAssemblies Create(
        IEnumerable<KeyValuePair<string, Version?>> assemblies,
        IEnumerable<KeyValuePair<string, NuGetVersion>>? packages = null,
        string description = "explicit")
        => new(
            assemblies.ToDictionary(a => a.Key, a => (Func<Version?>)(() => a.Value), StringComparer.OrdinalIgnoreCase),
            (packages ?? []).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            description);

    /// <summary>
    /// Discovers what the running tool provides: the framework it runs on, the assemblies next to it, and (when its
    /// <c>deps.json</c> is there) its packages.
    /// </summary>
    /// <param name="tool">The assembly of the tool, whose <c>deps.json</c> lists its packages (default: the entry assembly).</param>
    public static HostAssemblies Discover(Assembly? tool = null)
    {
        tool ??= Assembly.GetEntryAssembly() ?? typeof(HostAssemblies).Assembly;
        var assemblies = new Dictionary<string, Func<Version?>>(StringComparer.OrdinalIgnoreCase);
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var captured = path;
            var lazy = new Lazy<Version?>(() => ReadAssemblyVersion(captured));
            assemblies.TryAdd(Path.GetFileNameWithoutExtension(path), () => lazy.Value);
        }

        var packages = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var depsFile = Path.Combine(AppContext.BaseDirectory, tool.GetName().Name + ".deps.json");
        var description = "loaded assemblies only; deps.json not found";
        if (File.Exists(depsFile) && TryReadPackages(depsFile, packages))
            description = Path.GetFileName(depsFile);

        return new HostAssemblies(assemblies, packages, description);
    }

    private static bool TryReadPackages(string depsFile, Dictionary<string, NuGetVersion> packages)
    {
        try
        {
            using var stream = File.OpenRead(depsFile);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out var libraries))
                return false;

            foreach (var library in libraries.EnumerateObject())
            {
                var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is not ("package" or "project"))
                    continue;
                var slash = library.Name.LastIndexOf('/');
                if (slash > 0 && NuGetVersion.TryParse(library.Name[(slash + 1)..], out var version))
                    packages[library.Name[..slash]] = version;
            }
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the assembly version from a managed assembly; null for native or unreadable files.
    /// </summary>
    public static Version? ReadAssemblyVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadAssemblyVersion(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the assembly version from a managed assembly; null for native or unreadable content.
    /// </summary>
    public static Version? ReadAssemblyVersion(Stream stream)
    {
        try
        {
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            return pe.HasMetadata
                ? pe.GetMetadataReader().GetAssemblyDefinition().Version
                : null;
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
