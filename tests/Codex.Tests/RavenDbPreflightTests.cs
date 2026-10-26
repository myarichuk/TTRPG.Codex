using Codex.Persistence;
using Xunit;

namespace Codex.Tests;

public class RavenDbPreflightTests
{
    [Fact]
    public void A_writable_folder_passes()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-preflight-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(RavenDbService.CheckWritable(Path.Combine(root, "RavenData")));
            Assert.True(Directory.Exists(Path.Combine(root, "RavenServer")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_path_that_cannot_be_created_names_the_folder()
    {
        // A file where the folder should be: creating a directory under it always fails, even as root.
        var file = Path.GetTempFileName();
        try
        {
            var problem = RavenDbService.CheckWritable(Path.Combine(file, "RavenData"));
            Assert.NotNull(problem);
            Assert.Contains("RavenData", problem);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
