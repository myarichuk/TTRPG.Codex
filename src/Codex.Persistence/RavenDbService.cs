using Raven.Client.Documents;
using Raven.Embedded;

namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly IDocumentStore _store;

    public IDocumentStore Store => _store;

    public RavenDbService(string dataDirectory)
    {
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX", "2");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_PRE_RELEASE", "1");
        Environment.SetEnvironmentVariable("RAVEN_Server_FrameworkVersion", "10.0.0");

        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
            FrameworkVersion = "10.0.0",
            ServerUrl = "http://127.0.0.1:0",
            DotNetPath = "dotnet"
        };
        options.CommandLineArgs.Add("--Setup.Mode=None");

        // This forces RavenDB embedded server to use roll forward since it demands 10.0.2 but only 10.0.0 is available
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");

        // Explicitly rewrite the runtimeconfig.json file for the embedded server
        // This is a known workaround for RavenDB embedded forcing a specific preview patch version
        var ravenServerDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RavenDBServer");
        var runtimeConfigPath = Path.Combine(ravenServerDir, "Raven.Server.runtimeconfig.json");

        if (File.Exists(runtimeConfigPath))
        {
            var configContent = File.ReadAllText(runtimeConfigPath);
            if (configContent.Contains("10.0.2"))
            {
                configContent = configContent.Replace("10.0.2", "10.0.0");
                File.WriteAllText(runtimeConfigPath, configContent);
            }
        }

        try
        {
            EmbeddedServer.Instance.StartServer(options);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("The server was already started"))
        {
            // Ignore if already started (for xUnit parallel tests)
        }

        var databaseOptions = new DatabaseOptions("Campaigns");

        // This makes it so we can create individual document stores per context so multiple instances point at their respective temp folders in parallel.
        if (dataDirectory.Contains("TestRavenData_"))
        {
             // Unique database name per test context
             databaseOptions = new DatabaseOptions("Campaigns_" + Guid.NewGuid().ToString());
        }

        _store = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
