using System.Reflection;
using NuGet.Versioning;

namespace BitWizards.Plugins.Installation;

public static class ToolVersion
{
    /// <summary>
    /// Whether the assembly's informational version (e.g. 0.5.0-beta.1+abc123) is a prerelease version.
    /// </summary>
    public static bool IsPrerelease(Assembly assembly)
        => IsPrerelease(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static bool IsPrerelease(string? informationalVersion)
        => informationalVersion is not null
           && NuGetVersion.TryParse(informationalVersion.Split('+')[0], out var version)
           && version.IsPrerelease;
}
