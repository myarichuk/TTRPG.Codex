using Codex.Launcher;
using Xunit;

namespace Codex.Tests;

public class LauncherStartupProblemTests
{
    [Fact]
    public async Task A_server_that_cannot_write_its_data_folder_tells_the_user_why()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // needs a folder we can make read-only

        var home = Path.Combine(Path.GetTempPath(), "codex-startup-" + Guid.NewGuid().ToString("N"));
        var raven = LauncherPaths.RavenData(home);
        Directory.CreateDirectory(raven);
        // The launcher's own folders stay writable; only the database folder is read-only.
        Directory.CreateDirectory(Path.Combine(home, "logs"));
        Directory.CreateDirectory(LauncherPaths.MyContent(home));
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(raven, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var controller = new ServerController(AppContext.BaseDirectory, home);
            controller.Start(new LauncherSettings { Port = 5199 });

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (controller.State != ServerState.Failed && DateTime.UtcNow < deadline) await Task.Delay(200);

            Assert.Equal(ServerState.Failed, controller.State);
            Assert.Contains("cannot write", controller.Problem);
        }
        finally
        {
            File.SetUnixFileMode(raven, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(home, recursive: true);
        }
    }
}
