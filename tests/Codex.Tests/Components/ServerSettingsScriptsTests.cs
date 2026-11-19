using Bunit;
using Codex.Persistence;
using Codex.Web.Components.Admin;

namespace Codex.Tests.Components;

/// <summary>The admin's switch for game system scripts (<see cref="ServerSettingsForm"/>): what it says, what it shows until the admin chooses,
/// and what choosing stores.</summary>
public sealed class ServerSettingsScriptsTests : ComponentTestBase
{
    private IRenderedComponent<ServerSettingsForm> Render(ServerSettingsDocument value, bool configured = true, ServerSettingsForm.SettingsPart part = ServerSettingsForm.SettingsPart.Scripts) =>
        Render<ServerSettingsForm>(p => p.Add(c => c.Value, value).Add(c => c.Part, part).Add(c => c.ScriptsConfigured, configured));

    [Fact]
    public void TheSwitch_IsLabelled_AndTheExplanationIsInViewForAssistiveTechnologyToo()
    {
        var cut = Render(new ServerSettingsDocument());

        var box = cut.Find(Id("scripts-on"));
        Assert.Equal("scripts-on-hint", box.GetAttribute("aria-describedby"));
        Assert.Equal("Let game systems run their scripts", cut.Find("label[for=scripts-on]").TextContent.Trim());
        var hint = cut.Find(Id("scripts-on-hint")).TextContent;
        Assert.Contains("locked-down sandbox", hint);
        Assert.Contains("can't read your files, reach the internet or see other campaigns", hint);
        Assert.Contains("the Game Master does the scripted rules by hand", hint);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)] // the configuration says off, so it shows off
    [InlineData(true, false, true)] // the admin's choice wins over it
    [InlineData(false, true, false)]
    public void TheSwitch_ShowsTheAdminsChoice_ElseTheConfiguration(bool? stored, bool configured, bool shownOn)
    {
        var cut = Render(new ServerSettingsDocument { SystemScripts = stored }, configured);

        Assert.Equal(shownOn, cut.Find(Id("scripts-on")).HasAttribute("checked"));
    }

    [Fact]
    public void Choosing_StoresTheChoice_AndLeavingItAloneStoresNothing()
    {
        var value = new ServerSettingsDocument();
        var cut = Render(value);
        Assert.Null(value.SystemScripts);

        cut.Find(Id("scripts-on")).Change(false);
        Assert.False(value.SystemScripts);

        cut.Find(Id("scripts-on")).Change(true);
        Assert.True(value.SystemScripts);
    }

    [Fact]
    public void TheSwitch_IsOnTheAdminPage_ButNotOneOfTheSetupSteps()
    {
        var all = Render(new ServerSettingsDocument(), part: ServerSettingsForm.SettingsPart.All);
        var setup = Render(new ServerSettingsDocument(), part: ServerSettingsForm.SettingsPart.SignUp);

        Assert.NotEmpty(all.FindAll(Id("settings-scripts")));
        Assert.Empty(setup.FindAll(Id("settings-scripts")));
    }
}
