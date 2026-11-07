using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web;

/// <summary>The Playwright suites' seeding endpoints (<see cref="TestSeedUser"/>, <see cref="TestSeedActor"/>). Mapped only when
/// <c>Codex:EnableTestSeedEndpoint</c> is on: they write accounts and campaigns for anyone who calls them.</summary>
public static class TestSeedEndpoints
{
    public static void MapTestSeeding(this WebApplication app)
    {
        app.MapPost("/test/seed/users", async (List<Codex.Web.TestSeedUser> users, IUserRepository userRepository, IServerSettingsStore settings) =>
        {
            // A seeded server skips the first-time setup and lets anyone sign up and create campaigns, as servers did before
            // approval and roles; a test of those rules sets them with /test/seed/settings.
            if (!(await settings.GetAsync()).SetupDone)
            {
                await settings.UpdateAsync(s =>
                {
                    s.SetupDone = true;
                    s.SignUp = SignUpRule.Open;
                    s.CampaignCreation = CampaignCreationRule.Anyone;
                    s.WebFetch = WebFetchRule.AnyPublic;
                });
            }

            foreach (var seed in users)
            {
                var user = new UserDocument { Id = seed.Id, Username = seed.Username, Roles = seed.Roles };
                user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<UserDocument>().HashPassword(user, seed.Password);
                await userRepository.TryReserveUsernameAsync(seed.Username, seed.Id);
                await userRepository.CreateUserAsync(user);
            }

            return Results.Ok();
        }).AllowAnonymous();

        app.MapPost("/test/seed/settings", async (ServerSettingsDocument wanted, IServerSettingsStore settings) =>
        {
            await settings.UpdateAsync(s =>
            {
                s.SetupDone = wanted.SetupDone;
                s.SignUp = wanted.SignUp;
                s.CampaignCreation = wanted.CampaignCreation;
                s.ServerAi = wanted.ServerAi;
                s.WebFetch = wanted.WebFetch;
                s.FetchSites = wanted.FetchSites.ToList();
            });
            return Results.Ok();
        }).AllowAnonymous();

        app.MapPost("/test/seed/actors", async (List<Codex.Web.TestSeedActor> actors, IActorRepository actorRepository, ICampaignRepository campaignRepository, ComponentRegistry componentRegistry, ISystemCatalog catalog) =>
        {
            foreach (var seed in actors)
            {
                var campaign = await campaignRepository.GetAsync(seed.CampaignId);
                var system = campaign != null ? catalog.GetDefinition(campaign.SystemId) : null;
                var actor = new ActorDocument
                {
                    Id = seed.Id,
                    CampaignId = seed.CampaignId,
                    Kind = seed.Kind,
                    OwnerUserId = seed.OwnerUserId,
                    Name = seed.Name,
                    Visibility = seed.Visibility,
                    PublicName = seed.PublicName,
                    PublicDescription = seed.PublicDescription,
                    Biography = seed.Biography
                };

                foreach (var (key, value) in seed.Sheet ?? new Dictionary<string, object>())
                {
                    actor.Sheet[key] = value is System.Text.Json.JsonElement element ? element.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
                        System.Text.Json.JsonValueKind.True => true,
                        System.Text.Json.JsonValueKind.False => false,
                        _ => element.ToString()
                    } : value;
                }

                var primary = SystemDefinition.PrimaryKeyOf(system);
                actor.Sheet[primary] = (long)seed.Hp;
                actor.Sheet[SystemDefinition.PoolMaxKey(system, primary)] = (long)seed.HpMax;
                ActorSheetSync.ApplySheetToState(actor, system, componentRegistry);
                await actorRepository.SaveAsync(actor);
            }

            return Results.Ok();
        }).AllowAnonymous();

        // The session log isn't shown verbatim in any UI, so the e2e suite reads it back directly.
        app.MapGet("/test/inspect/session-events/{campaignId}", async (string campaignId, ICampaignRepository campaignRepository, ISessionRepository sessionRepository) =>
        {
            var campaign = await campaignRepository.GetAsync(campaignId);
            if (campaign?.CurrentSessionId == null)
            {
                return Results.Ok(Array.Empty<string>());
            }

            var session = await sessionRepository.GetAsync(campaign.CurrentSessionId);
            return Results.Ok(session?.Events.Select(e => e.Type).ToList() ?? new List<string>());
        }).AllowAnonymous();
    }
}
