using Raven.Client.Documents;
using Raven.Embedded;

namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly IDocumentStore _store;

    public IDocumentStore Store => _store;

    public RavenDbService(string dataDirectory)
    {
        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
            FrameworkVersion = "10.0.0"
        };

        // This is a workaround for RavenDB embedded trying to launch with '10.0.2' runtime instead of '10.0.0'
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX", "2");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_PRE_RELEASE", "1");
        // Create runtimeconfig.json for raven to force 10.0.0
        var configJson = @"{
  ""runtimeOptions"": {
    ""tfm"": ""net10.0"",
    ""framework"": {
      ""name"": ""Microsoft.AspNetCore.App"",
      ""version"": ""10.0.0""
    },
    ""rollForward"": ""LatestMajor""
  }
}";
        var serverDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RavenDBServer");
        if (!Directory.Exists(serverDir)) Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(serverDir, "Raven.Server.runtimeconfig.json"), configJson);


        try
        {
            EmbeddedServer.Instance.StartServer(options);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("The server was already started"))
        {
            // Ignore if already started (for xUnit parallel tests)
        }

        _store = EmbeddedServer.Instance.GetDocumentStore(new DatabaseOptions("Campaigns"));
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
