using System.CommandLine;
using System.Reflection;

namespace BitWizards.Plugins.Installation;

/// <summary>
/// The <c>install</c> command: installs a NuGet package, with its dependencies, into the plugin folder of the tool,
/// where the next run of the tool loads it. What the tool provides itself is not installed again.
/// </summary>
internal sealed class PluginInstallCommandConfigurator(PluginCatalog catalog, PluginHostOptions options) : IRootCommandConfigurator
{
    public void ConfigureRootCommand(RootCommand rootCommand)
    {
        var package = new Option<string>("--package", "--pkg") { Required = true, Description = "The id of the package" };
        var version = new Option<string?>("--version", "-v") { Description = "The version of the package, or latest (default)" };
        var source = new Option<string[]>("--source")
        {
            Description = "NuGet package source (a name from nuget.config, a URL or a folder); overrides the configured sources. Repeat it for more."
        };
        var configFile = new Option<string?>("--configfile") { Description = "The NuGet config file to use instead of the nuget.config lookup" };
        var clean = new Option<bool>("--clean") { Description = "Empty the plugin folder before installing", Arity = ArgumentArity.Zero };
        var includeProvided = new Option<bool>("--include-provided")
        {
            Description = "Also install packages and assemblies that the tool already provides",
            Arity = ArgumentArity.Zero
        };
        var prerelease = new Option<bool>("--prerelease")
        {
            Description = "Also consider prerelease package versions (always on when the tool itself is a prerelease)",
            Arity = ArgumentArity.Zero
        };

        var install = new Command("install", "Install a plugin package, with its dependencies, into the plugin folder")
        {
            Options = { package, version, source, configFile, clean, includeProvided, prerelease }
        };
        install.SetAction(async (parse, _) =>
        {
            var error = parse.InvocationConfiguration.Error;
            var id = parse.GetValue(package)!;
            var wanted = parse.GetValue(version) ?? "latest";
            var tool = options.ToolAssembly ?? Assembly.GetEntryAssembly() ?? typeof(PluginHost).Assembly;
            try
            {
                if (parse.GetValue(clean))
                    PluginCleaner.Clean(catalog.Directory,
                        [AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)], error);

                var feeds = await NuGetFeeds.LoadAsync(Directory.GetCurrentDirectory(), parse.GetValue(source), parse.GetValue(configFile), error);
                var host = parse.GetValue(includeProvided) ? null : HostAssemblies.Discover(tool);

                var toolIsPrerelease = ToolVersion.IsPrerelease(tool);
                if (toolIsPrerelease)
                    await error.WriteLineAsync("The tool is a prerelease; prerelease packages are allowed.");

                var success = await PluginInstaller.InstallAsync(id, wanted, catalog.Directory, feeds, host, error,
                    allowPrerelease: toolIsPrerelease || parse.GetValue(prerelease));
                await error.WriteLineAsync(success ? "Package installed successfully." : $"Package {id} {wanted} installation failed.");
                return success ? 0 : 1;
            }
            catch (Exception e) when (e is InvalidOperationException or FileNotFoundException)
            {
                await error.WriteLineAsync($"Package {id} {wanted} installation failed: {e.Message}");
                return 1;
            }
        });
        rootCommand.Subcommands.Add(install);
    }
}
