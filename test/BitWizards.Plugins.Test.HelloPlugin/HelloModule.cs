using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: BitWizards.Plugins.PluginModule<BitWizards.Plugins.Test.HelloPlugin.HelloModule>]

namespace BitWizards.Plugins.Test.HelloPlugin;

/// <summary>A plugin that adds a service, a command that greets as the configuration says, and a configuration value of its own.</summary>
public sealed class HelloModule : PluginModule
{
    public override void Configure(IConfigurationBuilder configurationBuilder)
        => configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?> { ["Hello:From"] = "the plugin" });

    public override void RegisterServices(IConfiguration configuration, IServiceCollection services)
    {
        services.AddSingleton(new Greeter(configuration["Hello:Greeting"] ?? "hello", configuration["Hello:From"] ?? "?"));
        services.AddTransient<IRootCommandConfigurator, HelloCommands>();
    }
}

public sealed record Greeter(string Greeting, string From);

internal sealed class HelloCommands(Greeter greeter, IContext<Command> context) : IRootCommandConfigurator
{
    public void ConfigureRootCommand(RootCommand rootCommand)
    {
        var hello = new Command("hello", "Greets");
        hello.SetAction(parse =>
        {
            parse.InvocationConfiguration.Output.WriteLine($"{greeter.Greeting} from {greeter.From} ({context.Context?.Name})");
            return 0;
        });
        rootCommand.Subcommands.Add(hello);
    }
}
