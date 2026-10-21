using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace BitWizards.Plugins;

/// <summary>
/// Loads the assemblies of a plugin folder into the default load context, and creates the modules they declare.
/// What the tool has itself wins over a copy in the folder, so that a type that is shared between the tool and a plugin is one type.
/// A file that can not be used is a warning in the <see cref="PluginCatalog"/>, not a failure.
/// </summary>
public static class PluginLoader
{
    /// <summary>The simple names of the assemblies of the tool and of the shared framework it runs on.</summary>
    public static HashSet<string> HostAssemblyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { AppContext.BaseDirectory, RuntimeEnvironment.GetRuntimeDirectory() })
            if (Directory.Exists(directory))
                foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
                    names.Add(Path.GetFileNameWithoutExtension(file));
        return names;
    }

    public static IPluginModule[] LoadModules(string directory, PluginHostOptions options, PluginCatalog catalog)
    {
        if (!Directory.Exists(directory))
            return [];

        var alreadyLoaded = AssemblyLoadContext.Default.Assemblies
            .Where(a => !a.IsDynamic)
            .GroupBy(a => a.GetName().Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var hostFiles = HostAssemblyNames();

        // every assembly is loaded before any module is created: a module may need an assembly that sorts after it,
        // and the default load context does not probe the plugin folder
        var assemblies = new List<(string File, Assembly Assembly)>();
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (hostFiles.Contains(name))
                continue;
            // an assembly the tool has wins, unless it is this very file or an identical copy (a second run in one process, say)
            if (alreadyLoaded.TryGetValue(name, out var existing) && !SameAssembly(existing, dll))
                continue;
            try
            {
                assemblies.Add((dll, existing ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(dll)));
            }
            catch (FileLoadException) when (LoadedAs(name) is { } loaded && SameAssembly(loaded, dll))
            {
                // another thread loaded it in the meantime
                assemblies.Add((dll, loaded));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // native libraries of a package (runtimes/...) are not assemblies, and are not a problem
                if (!(ex is BadImageFormatException && IsUnderRuntimes(directory, dll)))
                    catalog.Warnings.Add($"Could not load '{dll}': {ex.Message}");
                catalog.Plugins.Add(new PluginInfo(dll, null, null, [], PluginStatus.NotLoaded, ex.GetType().Name));
            }
        }

        return assemblies.SelectMany(a => Inspect(a.Assembly, a.File, options, catalog)).ToArray();
    }

    /// <summary>Creates the modules that an assembly declares, and records what became of it in the catalog.</summary>
    internal static IPluginModule[] Inspect(Assembly assembly, string file, PluginHostOptions options, PluginCatalog catalog)
    {
        var name = assembly.GetName();
        try
        {
            var attributes = assembly.GetCustomAttributes<PluginModuleAttribute>().ToArray();
            var discovered = options.ModuleDiscovery.SelectMany(discover => discover(assembly)).ToArray();
            if (attributes.Length == 0 && discovered.Length == 0)
            {
                catalog.Plugins.Add(new PluginInfo(file, name.Name, name.Version, [], PluginStatus.NoModule));
                return [];
            }

            foreach (var wanted in options.Contracts)
            {
                if (assembly.GetReferencedAssemblies().FirstOrDefault(r => r.Name == wanted.Name)?.Version is { } built && built.Major != wanted.Version?.Major)
                {
                    catalog.Warnings.Add($"Plugin '{name.Name}' is built against {wanted.Name} {built}, but the tool has {wanted.Version}: skipped.");
                    catalog.Plugins.Add(new PluginInfo(file, name.Name, name.Version, [], PluginStatus.IncompatibleContract, $"{wanted.Name} {built}"));
                    return [];
                }
            }

            var modules = attributes.Select(a => a.CreateModule()).Concat(discovered).ToArray();
            catalog.Plugins.Add(new PluginInfo(file, name.Name, name.Version, modules.Select(NameOf).ToArray(), PluginStatus.Loaded));
            return modules;
        }
        catch (Exception ex)
        {
            var message = ex is ReflectionTypeLoadException load ? string.Join("; ", load.LoaderExceptions.Select(e => e?.Message)) : ex.Message;
            catalog.Warnings.Add($"Could not initialize plugin '{file}': {message}");
            catalog.Plugins.Add(new PluginInfo(file, name.Name, name.Version, [], PluginStatus.Failed, ex.GetType().Name));
            return [];
        }
    }

    /// <summary>The name of the type of a module, which for a wrapper is that of the module it wraps.</summary>
    internal static string NameOf(IPluginModule module)
    {
        var type = (module is IPluginModuleWrapper wrapper ? wrapper.Module : module).GetType();
        return type.FullName ?? type.Name;
    }

    private static Assembly? LoadedAs(string name)
        => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool SameAssembly(Assembly loaded, string file)
        => string.Equals(Path.GetFullPath(loaded.Location), Path.GetFullPath(file), StringComparison.Ordinal)
           || ReadMvid(file) is { } mvid && mvid == loaded.ManifestModule.ModuleVersionId;

    private static Guid? ReadMvid(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            return pe.HasMetadata ? pe.GetMetadataReader().GetGuid(pe.GetMetadataReader().GetModuleDefinition().Mvid) : null;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException)
        {
            return null;
        }
    }

    private static bool IsUnderRuntimes(string directory, string dll)
        => Path.GetRelativePath(directory, dll).Split(Path.DirectorySeparatorChar).Contains("runtimes");
}
