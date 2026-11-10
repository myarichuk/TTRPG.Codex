using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Tests;

public class BackupFailuresTest
{
    // RavenDB's own exception type is internal to the server; one named the same stands in for it.
    private sealed class LicenseLimitException() : Exception("Your license doesn't support adding periodic backups");

    [Fact]
    public void TheLicenceCheckBeforeAskingAndRavenDbsOwnRefusal_BothMeanNotLicensed_AnythingElseIsAFailure()
    {
        Assert.True(BackupFailures.IsLicenceRefusal(new BackupNotLicensedException()));
        Assert.True(BackupFailures.IsLicenceRefusal(new InvalidOperationException("wrapped", new LicenseLimitException())));
        Assert.False(BackupFailures.IsLicenceRefusal(new IOException("disk full")));
    }
}
