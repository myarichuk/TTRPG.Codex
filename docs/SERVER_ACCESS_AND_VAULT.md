# Server access, roles and the character vault

Design agreed 2026-11-07, not built yet. It covers who may use a server and what for: the first-time setup, server roles and approval,
co-GMs, where the server may fetch from, players' own AI keys, and characters that live outside any campaign.

## 1. What is there today

- **First admin**: the first account made from this computer takes the ServerAdmin seat, never while the server is open to the internet
  (`ExposurePolicy.MayClaimFirstAdmin`, `IUserRepository.TryClaimFirstAdminAsync`). `Codex:AdminUsername` names an admin from anywhere.
- **Server roles**: `UserRoles.Player` and `UserRoles.ServerAdmin`. Anyone signed in may create a campaign.
- **Sign-up**: open, except while the server is open to the internet, when a new account needs a real campaign invite
  (`ExposurePolicy.MaySignUpAsync`). External sign-in exists.
- **Campaign roles**: `CampaignRole.DM` and `Player` per member; `CampaignDocument.OwnerId` is the campaign's creator. Several members could
  already hold `DM` in the data, but nothing offers it, and some code reads `OwnerId` as "the GM".
- **AI**: every user keeps their own providers and keys (`AiProviderService`, `/ai-settings`); the server's own key is admin-only while open
  to the internet (`ExposurePolicy.MayUseServerAi`), with a daily allowance (`ServerAiUsage`). Players can already, on their own key: create
  their character from a concept in a campaign (`AiFeatures.CreateCharacterAsync`), rewrite its history (`RewriteAsync`) and ask what the
  next level brings (`LevelUpAsync`). GMs create NPCs from a Library creature's stat block, an AI draft or a blank sheet
  (`CastService.AddNpcAsync`), and can lay a stat block over a character (`CastService.Restat`).
- **Web fetching** for book stylesheets (docs/BOOK_EXPORT.md 41.14): any GM, any public https address, bounded.
- **Characters**: an `ActorDocument` always has a `CampaignId`.

## 2. First-time setup

Shown while the server has no accounts, and only on this computer (the same rule as the first admin seat). Each step after the first has a
default and can be skipped; all of it can be changed later on the admin's server settings page.

1. **The admin account.**
2. **Server name and language.**
3. **Who can sign up**: open, needs approval (preselected), or invite only.
4. **Who may create campaigns**: anyone approved, or only Game Masters (preselected).
5. **Where book stylesheets may fetch from**: a list of sites (preselected, with the defaults in 5, editable right here) or any public site.
6. **The server's AI key** (optional), and which roles may use it.
7. **Game systems** to install.

A forgotten admin password has no email to recover it: the launcher offers "reset the admin's password", run on this computer only.

## 3. Roles and approval

- **Server roles**: Admin, Game Master (may create campaigns), Player (may join campaigns). An admin is also a Game Master.
- **Sign-up rule** (setup step 3): open, approval, invite only.
  - With approval, a new account can sign in but sees only "waiting for approval" until an admin approves or rejects it.
  - Arriving with a campaign invite from an approved Game Master counts as approval as a Player.
  - External sign-in goes through the same rule.
  - While the server is open to the internet, "open" behaves as "approval".
- **Admin page**: accounts (waiting first), approve, reject, change role, disable, delete; a badge in the app when someone is waiting.
- **Existing servers**: every existing account is approved; everyone who owns a campaign becomes a Game Master.
- **Admin log**: approvals, rejections, role changes, deletions, settings changes, with who and when.

## 4. Co-GMs

- A campaign's GM can make one of its players a **co-GM** (and take it back). It is a campaign role, so it needs no server role: a Player can
  be a co-GM in someone else's campaign without being allowed to create campaigns.
- A co-GM can do what the GM does (sees GM notes, hidden pages and characters; runs the table; edits the world and the book), except:
  delete the campaign, pass it to someone else, make or remove co-GMs, and remove the owner.
- The GM is told plainly, when promoting, that a co-GM sees every secret.
- `OwnerId` stays the one owner; checks that mean "may GM" move to the role, checks that mean "owns" stay on `OwnerId`.
- Passing a campaign to another GM (also needed when deleting an account, 9) uses the same role change.

## 5. Where stylesheets may fetch from

- Setup step 5 chooses a **site list** (the default) or **any public site**. The list is matched by domain suffix and checked at every
  redirect, on top of the existing checks (https, public addresses only, bounds, sniffing).
- **Default list**: `fonts.googleapis.com`, `fonts.gstatic.com`, `use.typekit.net`, `i.imgur.com`, `cdn.jsdelivr.net`,
  `raw.githubusercontent.com`.
- An address not on the list fails with its own reason ("this site is not on the server's list; the admin can add it").
- Open: whether "any public site" should still fall back to the list while the server is open to the internet. Until decided, the admin's
  choice stands either way.

## 6. Players' own AI keys

Players already bring their own key; this widens what it is for, always built from what that player may see.

- **Notes**: a "tidy up" on a player's own notes (formatting, spelling, structure), never adding facts.
- **Character creation**, in a campaign and in the vault (8): the same flow as NPC creation. Start from a blank sheet, a concept (AI draft)
  or, for NPCs, a Library creature's stat block. Then tweak by hand or ask the AI for a change ("add a fire power", "make them a better
  climber"): the AI proposes changes to the sheet, picking Library entries where the system has them and homebrew text where it does not,
  and the person accepts or drops each one before anything is saved.
- **Structured, like the world builder chat**: character work runs on tools rather than one free-text answer. The model reads the sheet
  and the system's schema, lists the Library's real options (classes, species, skills, powers, items), and ends with a proposal of
  changes, each one a field and a value with a one-line reason. The same tools serve the companion MCP server when it comes.
- **Highlights** (on request): for a concept like "former pirate" or "ex-smuggler turned priest", the model marks what matters for the
  story (the attributes to favour, the skills, languages, tools and gear that fit) with a short reason each, shown on the sheet as
  suggestions the person can take or ignore. Highlights change nothing by themselves.
- **The server's key per role** (setup step 6): for example "Game Masters may use it", with the daily allowance.

## 7. Bringing a character into a campaign: a copy

- A vault character is brought in as a **copy** that remembers where it came from. The campaign's copy collects what belongs to that
  campaign (GM notes, history the GM revealed, items the GM moved, the table's state), so it must not flow into other campaigns.
- The player can **update the vault character from the campaign copy** at any time, only from the parts the player owns (sheet, history,
  portrait; never GM notes or revealed blocks). Pulling the vault version into a campaign later needs the GM's approval.
- **Before the copy**: the campaign must use the same system, and everything the character uses (class, species, items, homebrew) must be
  available in the campaign. What is missing is listed first, with the choice to leave it out or ask the GM to add it.
- **The GM accepts** a brought-in character before it joins the party.

## 8. The vault

- A vault character is an actor with no campaign and a system id, owned by the player. The vault page lists them, filtered by system.
- Created and edited like any character (6), against the content installed on the server for that system (the server's packs, no campaign
  homebrew).
- Rules and sheets come from the system, so a vault character needs no campaign to be a full sheet.

## 9. Also needed

- **Storage limits per account**: portraits, art, uploads, book copies; a total per account the admin sets, and a page showing who uses what.
- **Deleting an account**: campaigns it owns are passed to a co-GM or archived; its vault goes with it.
- **Exporting your own data**: a player's vault, a GM's campaigns.

## 10. Order of building

1. Setup wizard, server roles, approval, the admin page and log, migration of existing servers.
2. Co-GMs.
3. The fetch list.
4. The vault and bringing characters in.
5. AI: notes, character creation and tweaks in both places, the server key per role.
6. Storage limits, account deletion, export.

## 11. Step 1 as built (2026-11-07)

- **Data**: `UserDocument.Status` (`AccountStatus`: Approved, Pending, Disabled; a stored account with no value reads as Approved),
  `UserDocument.CreatedAt`, `UserRoles.GameMaster`; one `ServerSettingsDocument` (`server/settings`: setup done, sign-up rule, campaign
  rule, fetch rule and list) and `AdminLogDocument`s, through `IServerSettingsStore` (`RavenServerSettingsStore`, which keeps a copy).
- **Setup**: `/setup` (static, this computer only, no accounts yet) makes the admin (Player, Game Master, ServerAdmin) through the same
  first-admin seat as before, and stores the safe defaults (approval, Game Masters, the site list) at once; `/setup/server` (admin only)
  is three steps (sign-up, campaigns, fetching) and a last page linking AI settings, the Library and the admin page. A step is saved as it
  is passed; "Keep the rest as they are" finishes with what is set. `/login` and `/register` send to `/setup` while there are no
  accounts; elsewhere they say to open it on the server's computer. Server name, language, the AI key and game systems are not steps:
  the language is the launcher's, the AI key and systems have their own pages.
- **Sign-up** (`ServerAccess.NewAccount`): an invite approves at once; open to the internet, nothing else may sign up (as before); at
  home, Open approves, Approval waits, Invite only refuses. External sign-in follows the same rule.
- **Waiting**: a waiting account carries a `codex:status` claim; the fallback policy refuses it everywhere but `/waiting`, `/logout`,
  `/set-culture`, sign-in pages and an invite link (`/campaigns/join/…`, now a static page), which joins and approves it, logged as the
  campaign GM's approval.
- **Staying true**: the cookie's `OnValidatePrincipal` (`AccountCheck`) re-reads the account (cached ten seconds, forgotten when the admin
  changes it): disabled or deleted is signed out, a changed status or role gets a new principal. An open page re-checks every 30 seconds
  (`AccountRevalidation`) and ends on disabled, deleted or waiting; a role change shows on the next page load.
- **Campaigns**: `ServerAccess.MayCreateCampaigns` reads the stored account, on Home (button, empty state, create) and on restoring a
  campaign file as a new campaign.
- **Admin page** (`/admin`, header link with the waiting count): approve; reject (deletes the waiting account and frees its name);
  turn off and on; Game Master and admin checkboxes; delete only an account in no campaign (passing campaigns on is step 6); never the
  last admin, and not yourself. Settings (same form as the setup), and the last 100 log entries. Setting changes are logged in English
  setting names (`sign-up: Approval → Open`).
- **Existing servers** (`MoveExistingServerAsync`, at start-up, once): accounts stay approved, campaign owners and admins become Game
  Masters, fetching stays "any public site", sign-up becomes "needs approval", setup counts as done.
- **Fetch list**: `BookWebFetcher` asks the settings at every hop (`BookWebProblem.NotListed`, "x is not on the server's list of sites");
  whole-label suffix match; defaults include `rawcdn.githack.com` and `raw.githack.com`, which Tasha's Style uses.
- **Tests**: `ServerAccessTests` (rules, invite, principal, list matching, the fetcher at a redirect, the cookie check),
  `ServerSettingsStoreTests` (defaults, the move of an existing server and that it runs once, the list cleaned, the log order, a deleted
  name freed). Mutants caught: no cookie check; anyone creates; suffix match without the dot; the list checked at the first hop only; an
  invite ignored. The E2E fixture seeds an admin, which also makes the test server permissive (`/test/seed/settings` changes it).
- **Not done**: the launcher's "reset the admin's password"; the admin log shows setting changes in English; an open page shows a new role
  only after a reload; deleting an account that is in a campaign.
