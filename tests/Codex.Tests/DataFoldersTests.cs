using Codex.Launcher;
using Codex.Web;
using Xunit;

namespace Codex.Tests;

public class DataFoldersTests : IDisposable
{
    private readonly string root = Path.Combine(TestTempData.Root, TestTempData.OwnedName("DataFolders_"));

    public void Dispose() => TestTempData.DeleteDirectory(root);

    private string At(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());

    [Fact]
    public void With_nothing_set_the_data_goes_to_the_per_user_folder_and_the_keys_sit_beside_it()
    {
        var folders = DataFolders.Resolve(null, null, At("app"), userHome: At("home"));

        Assert.Equal(At("home", "RavenData"), folders.Data);
        Assert.Equal(At("home", "Keys"), folders.Keys);
        Assert.Equal(At("home", "RavenData", "Backups"), folders.Backups);
    }

    [Fact]
    public void A_relative_setting_is_taken_from_the_content_root_not_the_working_folder()
    {
        var folders = DataFolders.Resolve("data/RavenData", null, At("app"));

        Assert.Equal(At("app", "data", "RavenData"), folders.Data);
        Assert.Equal(At("app", "data", "Keys"), folders.Keys);
    }

    [Fact]
    public void The_key_folder_can_be_set_on_its_own()
    {
        var folders = DataFolders.Resolve(At("db") + Path.DirectorySeparatorChar, At("secrets"), At("app"));

        Assert.Equal(At("db"), folders.Data);
        Assert.Equal(At("secrets"), folders.Keys);
    }

    [Fact]
    public void The_server_and_the_launcher_agree_on_the_per_user_folder()
    {
        Assert.Equal(LauncherPaths.DataHome(), DataFolders.UserHome());
        Assert.Equal(LauncherPaths.RavenData(LauncherPaths.DataHome()), DataFolders.Resolve(null, null, At("app")).Data);
    }

    // An older dev run: started from src/Codex.Web, "RavenData" by default; Raven put the database under its own folder in bin/,
    // the key ring and the JSON backups went under the working folder.
    private (LegacyDataMigration.Sources Sources, DataFolders Folders, string Raven, string Old) OldDevRun()
    {
        var app = At("repo", "src", "Codex.Web");
        var raven = Path.Combine(app, "bin", "Debug", "RavenDBServer");
        var folders = DataFolders.Resolve(null, null, app, userHome: At("home"));
        return (LegacyDataMigration.Find(null, folders, app, app, raven), folders, Path.Combine(raven, "RavenData"), Path.Combine(app, "RavenData"));
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void An_older_dev_database_keys_and_backups_move_to_the_new_folders()
    {
        var (sources, folders, raven, old) = OldDevRun();
        Write(Path.Combine(raven, "Databases", "Campaigns", "Raven.voron"), "campaigns");
        Write(Path.Combine(raven, "System", "Raven.voron"), "system");
        Write(Path.Combine(old, "Keys", "key-a.xml"), "ring a");
        Write(Path.Combine(old, "Backups", "2026-11-05_001234", "dump.json"));

        var done = LegacyDataMigration.Run(sources, folders);

        Assert.Equal("campaigns", File.ReadAllText(Path.Combine(folders.Data, "Databases", "Campaigns", "Raven.voron")));
        Assert.Equal("system", File.ReadAllText(Path.Combine(folders.Data, "System", "Raven.voron")));
        Assert.False(Directory.Exists(Path.Combine(raven, "Databases")));
        Assert.True(File.Exists(Path.Combine(raven, "MOVED.txt")));
        Assert.Equal("ring a", File.ReadAllText(Path.Combine(folders.Keys, "key-a.xml")));
        Assert.True(File.Exists(Path.Combine(old, "Keys", "key-a.xml")), "keys are copied, never moved");
        Assert.True(File.Exists(Path.Combine(folders.Backups, "2026-11-05_001234", "dump.json")));
        Assert.Equal(3, done.Count);
    }

    [Fact]
    public void Keys_from_every_old_ring_are_merged_without_overwriting()
    {
        var (sources, folders, _, old) = OldDevRun();
        Write(Path.Combine(old, "Keys", "key-a.xml"), "old a");
        Write(Path.Combine(old, "Keys", "key-b.xml"), "old b");
        Write(Path.Combine(folders.Data, "Keys", "key-c.xml"), "launcher c"); // where the last release kept its ring
        Write(Path.Combine(folders.Keys, "key-a.xml"), "new a");

        LegacyDataMigration.Run(sources, folders);

        Assert.Equal("new a", File.ReadAllText(Path.Combine(folders.Keys, "key-a.xml")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(folders.Keys, "key-b.xml")));
        Assert.Equal("launcher c", File.ReadAllText(Path.Combine(folders.Keys, "key-c.xml")));
    }

    [Fact]
    public void A_database_never_lands_on_top_of_another()
    {
        var (sources, folders, raven, _) = OldDevRun();
        Write(Path.Combine(raven, "Databases", "Campaigns", "Raven.voron"), "old");
        Write(Path.Combine(folders.Data, "Databases", "Campaigns", "Raven.voron"), "new");

        var done = LegacyDataMigration.Run(sources, folders);

        Assert.Equal("new", File.ReadAllText(Path.Combine(folders.Data, "Databases", "Campaigns", "Raven.voron")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(raven, "Databases", "Campaigns", "Raven.voron")));
        Assert.Contains(done, line => line.Contains("left both"));
    }

    [Fact]
    public void A_new_folder_holding_only_backups_still_takes_the_old_database()
    {
        // The installed app had made RavenData with an empty Backups folder before any campaign existed.
        var (sources, folders, raven, _) = OldDevRun();
        Directory.CreateDirectory(folders.Backups);
        Write(Path.Combine(raven, "Databases", "Campaigns", "Raven.voron"), "campaigns");

        LegacyDataMigration.Run(sources, folders);

        Assert.True(File.Exists(Path.Combine(folders.Data, "Databases", "Campaigns", "Raven.voron")));
    }

    [Fact]
    public void Running_again_changes_nothing()
    {
        var (sources, folders, raven, old) = OldDevRun();
        Write(Path.Combine(raven, "Databases", "Campaigns", "Raven.voron"));
        Write(Path.Combine(old, "Keys", "key-a.xml"));
        LegacyDataMigration.Run(sources, folders);

        Assert.Empty(LegacyDataMigration.Run(sources, folders));
    }

    [Fact]
    public void An_absolute_data_folder_has_no_older_database_to_look_for()
    {
        var folders = DataFolders.Resolve(At("db", "RavenData"), null, At("app"));
        var sources = LegacyDataMigration.Find(At("db", "RavenData"), folders, At("app"), At("cwd"), At("raven"));

        Assert.Null(sources.Database);
        Assert.Empty(sources.BackupFolders);
        Assert.Equal(new[] { At("db", "RavenData", "Keys") }, sources.KeyFolders);
    }
}
