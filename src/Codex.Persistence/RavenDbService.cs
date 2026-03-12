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

        // Use the currently executing framework version dynamically
        var version = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            .Replace(".NET ", "")
            .Split(' ')[0];

        // Or we hardcode 10.0.0 because memory strictly says FrameworkVersion="10.0.0"
        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
            FrameworkVersion = "10.0.0"
        };

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
