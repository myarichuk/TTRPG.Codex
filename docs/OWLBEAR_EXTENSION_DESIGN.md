# Owlbear Rodeo extension

Status: design draft, 2026-11-15. Nothing is built. Open questions are in §11.

Codex stays the campaign's memory and rules: sheets, conditions, combat, the wiki, secrets and places. Owlbear Rodeo stays the table:
the map, tokens, fog and the players' view. An Owlbear extension joins the two, so a group can play in Owlbear and the GM keeps
everything in Codex. Players don't need to change tools, and a Codex account is optional for them (§3).

Owlbear was picked first because it has no rules, sheets or notes of its own, so Codex fills a gap instead of clashing with
something built in. Foundry comes later and reuses the table API (§6).

## 1. What Owlbear gives an extension (checked 2026-11-15)

- **Free plan**: the free *Nestling* plan includes extensions, SDK access and dynamic lighting, with 200 MB of storage and 2 rooms.
  Testing costs nothing.
- **Installing**: an extension is a web page plus a `manifest.json`. The GM adds it from their Owlbear profile with the manifest's address
  (the tutorial uses `http://localhost:5173/manifest.json` during development), then turns it on for a room.
- **Each player's browser loads the extension from that same address.** A `localhost` address only works for the person whose machine
  serves it, so the published extension must live at a public address (§4).
- **Data the extension can keep in the room**:
  - each scene item (a token, a shape) and each player has a `metadata` object;
  - the room has metadata too, under 16 kB in total and shared with every other extension;
  - keys are namespaced, for example `io.github.myarichuk.codex/actor`.
- **Messages**: `OBR.broadcast.sendMessage` sends a message of up to 16 kB to other players, to this player only, or to everyone. Nothing is stored.
- **Roles**: `OBR.player.getRole()` says whether this browser is the GM or a player.
- **Images and scenes** (`OBR.assets`):
  - `uploadImages` puts images into the user's Owlbear storage, typed as map, prop, mount, character, attachment or note.
  - `uploadScenes` makes whole scenes in one go: a name, a base map image, a grid, fog settings and a list of starting items.
  - Both open a folder picker, so the GM makes one click per upload. Nothing is silent.
  - Uploads count against the GM's storage: 200 MB on the free plan, with images up to 25 MB and 67 megapixels.
  - The docs don't say that `uploadImages` returns the new images' addresses.
- **Items** (`OBR.scene.items`):
  - Every item type can be made from code: image, shape, curve, path, line, label, text, light, wall, effect, billboard, pointer, ruler.
  - Items go on layers (map, prop, mount, character, attachment, note, text, fog and more), can be hidden or locked, and can be
    attached to another item, so a badge moves with its token.
  - An image item loads from any address with CORS turned on. Every player's browser must be able to reach that address.
- **Local items** (`OBR.scene.local`):
  - The same items, but seen only by the browser that made them, and never synced.
  - This is where an extension can draw GM-only overlays (an NPC's real HP, a trap's trigger area) without sending them to players.
- **Walls and lights are local only.** Owlbear's own *Dynamic Fog* extension makes walls from the native fog shapes, which are ordinary
  synced items. Its door tool and light settings sit on top of those. So a map that arrives with fog shapes gets working walls in every
  player's browser, without Codex drawing walls itself.
- **Panels**: action (the extension's main panel), popover (anchored to an element), modal (centred, can be full screen), context-menu
  entries on items, custom tools on the toolbar, notifications. Popovers and modals are web pages embedded by address.
- **Not in the SDK**: dice, chat and a roll log. Owlbear's dice is a separate extension with no documented API, so other extensions'
  rolls can't be read reliably.
- Not yet checked: limits on item and scene metadata, and how the Dynamic Fog extension stores doors.

Anything written to metadata, broadcast or synced items is readable by every player's browser, even when the item is hidden. Secrets
never go there (§7).

## 2. Who owns what

Each piece has one owner, so the two sides never fight over the same data.

| Codex owns | Owlbear owns |
|---|---|
| Sheets, HP, conditions, resources | The scene, the map image, token positions |
| The combat console: order, turns, rounds | Fog, walls, lighting |
| Places, wiki pages, secrets, hazards | Who is in the room |
| What each player may see | Drawing, measuring, pings |

Owlbear sends Codex *requests* ("this token took 7 damage", "start combat with these tokens"). Codex applies them through its normal
services and the same permission checks, then sends back the result. Codex never moves tokens. Owlbear never edits a sheet directly.

### What Codex can own, and what it can't

Given §1, the extension can take over much more than sheets.

| Codex can own | How |
|---|---|
| **Rolls** | Owlbear has no dice API, so nothing to fight. Rolls are made in Codex from the sheet, with its bonuses, advantage and conditions. The result goes to the room's roll log in the panel and pops up as a notification. A GM roll can stay hidden. Animated dice are optional and come later. |
| **Sheets, HP, conditions, resources** | The sheet panel, popovers on tokens, right-click entries on tokens. |
| **Combat** | The order, turns and rounds come from the combat console. The current token is highlighted with an attached item. |
| **Token art** | Portraits and Bestiary art (§5.7). |
| **Whole maps** | A Codex map (uploaded or generated) becomes an Owlbear scene: base map, grid, fog shapes per room (walls through Dynamic Fog), Place pins and starting tokens (§5.6). |
| **Wiki and Places** | Pages shown in a popover or modal, as each viewer may see them (§5.5). |
| **Exploration** | Each room starts under its own fog shape. Revealing a Place clears its fog and shows its page to players. |
| **Handouts** | The GM shows one page or image to chosen players. |
| **GM-only overlays** | Drawn as local items in the GM's browser only: real NPC HP, trap areas, secret doors, the next room's notes. |
| **Condition and HP badges** | Attached items, with icons hosted next to the extension. |

| Owlbear keeps | Why |
|---|---|
| Drawing the scene, moving tokens, measuring | That's Owlbear's job, and doing it again gives players nothing. |
| Fog, walls, doors and lighting | Dynamic Fog already does them. Codex gives it fog shapes and leaves the rest. |
| Accounts, rooms, storage, chat | Owlbear's own. There's no API for chat anyway. |
| Other extensions' data | No documented API. A table that loves another dice extension keeps it. Codex's rolls are only the ones made through Codex. |

## 3. Two ways to connect

Codex already runs three ways: on this computer only, hosted on the home network, or open to the internet through Tailscale Funnel
(`ExposurePolicy`). The extension supports two links. The same table API covers both.

### 3a. GM link (the default)

Only the GM's browser talks to Codex. Codex can stay local-only.

1. In Codex, the GM opens the campaign's *Owlbear* page and makes a **link code**: one campaign, short-lived, single use.
2. In Owlbear, the GM pastes the Codex address and the code into the extension. They get a **table token** for that campaign. The
   token can be revoked, and it allows only the table API (§6).
3. The GM's copy of the extension asks Codex for the **party view**, which Codex builds with its normal player-visibility rules. It sends
   that view to the players' copies by broadcast. It also keeps a small snapshot in room metadata, so a player who joins late sees something.

What this costs: every player sees the same party view. A secret that only one player knows can't be shown just to them. If the GM's
browser closes, the players' panels stop updating at the last snapshot.

### 3b. Player links

When the server is reachable (home network or Funnel), each player can also link their own copy with their own Codex sign-in, in the
same way. Their copy then asks Codex directly, and Codex applies that player's own visibility: their sheet in full, their secrets,
their notes. A player with no link still gets the party view from the GM.

### Does running Codex locally make sense?

Yes. With the GM link it's arguably the best setup:
- Owlbear is in the cloud and free.
- Codex and its secrets stay on the GM's machine.
- Only data meant for players leaves it.

The catches:
- **Codex has to be running during the session**, on the same machine as the GM's browser, or at an address that browser can reach.
- **The browser may ask permission.** An `https://owlbear.rodeo` page calling `http://localhost` is a request from a public site to the
  local network. Recent Chrome asks the user for permission once, and Codex must answer the preflight. Safari's handling is unknown.
  Test it before building anything else (§10, step 0).
- **No per-player secrets** without player links (3b).
- **A Funnel address avoids the browser questions.** It's public `https`, so nothing is mixed. That's the fallback if the local call
  fails in a browser.

## 4. Where the extension lives

- **Source**: `integrations/owlbear/` in this repository. TypeScript, Vite and `@owlbear-rodeo/sdk`, as Owlbear's own examples use.
  The panel is small, so no UI framework unless one earns its place.
- **Hosting**: a static site on GitHub Pages, built by CI from `main`. Free, public, one address for every table. A second
  address built from a branch is for testing.
- **Codex doesn't serve the extension.** It serves only the table API, so a Codex update never breaks rooms that have the extension.
  The extension checks the API version on connect and says plainly when Codex is too old or too new.
- **Owlbear's extension store**: optional, later. During development the manifest address is enough.

## 5. What it does, in order of building

1. **Link and status.** Connect (§3), show the campaign name, the link state and the API version. Unlink.
2. **Tokens to characters.**
   - The GM right-clicks a token and picks *Link to Codex*, then chooses a party member, a cast NPC or a Bestiary creature.
     A creature makes a new NPC in the cast, as `CastService.AddNpcAsync` does now.
   - Only the Codex id goes into the token's metadata.
   - The token's name and HP bar follow Codex.
3. **Sheet panel and rolls.**
   - Select a token to see its sheet: in full for your own character or for the GM, a short version otherwise, using the rules the
     preview pages use (`PreviewEndpoints`).
   - Click a check, save, attack or spell to roll it in Codex. Results go to the panel's roll log for everyone and show as a
     notification. The GM can roll hidden.
   - In the GM link (§3a) a player's roll goes to the GM's copy by broadcast. Codex makes the roll, then the result is broadcast back.
     So no player's browser makes up its own numbers.
4. **Combat.**
   - *Start combat* with the selected tokens opens the encounter in the combat console.
   - Owlbear shows the order and whose turn it is, and highlights that token.
   - Damage, healing and conditions from Owlbear's right-click menu go to Codex as requests (§2). Conditions show as badges attached to the token.
   - NPCs' real HP is drawn as local items in the GM's browser only. Players see only what Codex puts in the party view, such as "bloodied".
   - The combat console stays usable at the same time. Both show the same encounter.
5. **Wiki, Places and exploration.**
   - **Reading pages.** A *Codex* entry on any token or pin opens its page in a popover. A full page opens in a modal. Codex returns the
     page already rendered as safe HTML, cut to what this viewer may see. The extension shows it in its own page. It doesn't embed the
     Codex site: that would need Codex's sign-in cookie inside Owlbear's frame, which Safari and Firefox block.
   - In the GM link, players get the pages the GM reveals, by broadcast. A long page goes in parts, since each message is up to 16 kB.
   - **Pins.** The GM marks a map area or a pin and links it to a Place. A pin holds only the Place's id.
   - **Exploration mode.** A GM switch. When a party token enters a marked area, the GM gets a prompt with that Place's secrets,
     hazards and creatures. *Reveal* clears the room's fog shape, shows the page to players and adds it to their journal in Codex.
   - **Handouts.** The GM shows a page or an image to chosen players.
6. **Scenes from Codex.**
   - The table API returns a **scene bundle** for a Codex map:
     - the map image;
     - its grid (type, cell size in pixels, offset);
     - room outlines and doors, and lights;
     - Place pins;
     - starting tokens with their Codex ids.
   - The extension turns it into one `uploadScenes` call:
     - the base map and grid;
     - one fog shape per room, which Dynamic Fog turns into walls;
     - lights on the light-giving items;
     - hidden pins;
     - tokens.
   - The GM picks a folder once, and the scene appears in their Owlbear storage.
   - Doors are an open question until we know how Dynamic Fog stores them (§11). Until then the GM adds doors with its door tool.
   - The same bundle can be exported as a `.uvtt` file for other VTTs. It's the same data: walls, doors, lights and grid.
   - Sources: a map uploaded to a Codex map page, and later the dungeon generator.
7. **Token art.**
   - **GM link with Codex on the GM's machine**: players can't reach Codex, so art has to live in Owlbear. *Upload party art* sends
     portraits and the encounter's creature art in one `uploadImages` call, typed as characters. The GM places tokens from their Owlbear
     library, and *Link to Codex* ties each token to its sheet. A scene bundle can carry its starting tokens' art with the base map.
   - **Codex reachable by everyone** (home network or Funnel): token images can load straight from Codex, with no upload and no storage
     used. Codex serves each image at a long, unguessable address with CORS turned on. The address can be revoked, and it shows nothing
     but the image. Art for hidden creatures is still readable by a player who digs, so creatures stay off the map until revealed.
   - AI-made art goes the same way, since Codex stores it like any other portrait.

Steps 1 to 4 are the first release. Steps 5 to 7 wait for map pages in Codex (docs/MAPS_DESIGN.md, M1 and M2), apart from
reading pages, which can come early.

## 6. The table API

A small, versioned JSON API under `/api/table/v1`, kept separate from the Blazor pages. It's written for any VTT, so a Foundry module
can use it later without changes.

- **Auth**: a table token (§3) in an `Authorization` header, never in a query string. The token belongs to one user and one campaign,
  and every call checks the user's campaign role as the pages do.
- **Reads**:
  - campaign summary;
  - party view;
  - one actor as this viewer may see it;
  - the encounter;
  - a wiki or Place page rendered as safe HTML for this viewer;
  - a scene bundle for a map (§5.6);
  - token art.
- **Writes**:
  - make a roll from a sheet (the server rolls, never the browser);
  - apply damage or healing;
  - add or remove a condition;
  - start or end an encounter, next turn;
  - link a creature into the cast;
  - reveal a Place or a page to players.
  Each write goes through the same services as the combat console and the wiki.
- **Live updates**: one Server-Sent Events stream per connection with small "this changed, here is the new version" events. The
  extension reads again when it needs to. SignalR is already in the app and is the alternative if SSE fights the Blazor hosting.
- **CORS**: allow only the extension's hosted address (§4) and the development address. Answer the private-network preflight
  (`Access-Control-Allow-Private-Network`).
- **Rate limits**: the same buckets as `ExposurePolicy` while open to the internet.

## 7. Secrets and safety

- Anything sent to a player's browser is readable by that player. Codex decides what goes into the party view and into each player's
  view. The extension never filters.
- The GM's copy only broadcasts what Codex marked for the party view. Hidden HP, NPC sheets, secrets and Place notes stay with the GM.
- Metadata holds ids and display values that the table already sees (name, HP bar, condition badges). Never notes.
- A hidden item is still sent to every player's browser. Secret pins, unrevealed rooms and unseen creatures carry only ids, or stay off
  the map. GM-only drawing uses local items (§1).
- Rolls are made by Codex on the server. A player's browser asks for a roll and never sends a result of its own.
- Table tokens are stored hashed in Codex. The GM can list and revoke them on the campaign's *Owlbear* page. Unlinking removes the
  token from the extension's storage.
- A table token can't sign in to the Codex site, change accounts or use AI keys.

## 8. Words and accessibility

- The panel uses Codex's own terms and its en, ru and he text. The extension carries its own small text files, and ru and he need
  native review as usual.
- It works by keyboard, with labels for screen readers, and in both of Owlbear's themes. Hebrew lays out right to left.
- Errors say what to do: "Codex isn't running at this address", "This link was revoked in Codex", "Codex is older than this extension:
  update Codex".

## 9. Tests

- **Table API**: integration tests in `tests/Codex.Tests`, like the endpoint tests now. Each read is checked as a GM, a player and a
  stranger. A player never receives another player's secret or an NPC's hidden HP.
- **Extension**: unit tests for the request and broadcast logic, run against a fake `OBR`.
- **End to end**: one manual check per release in a free Owlbear room, with the GM and a player in two browsers. A script for it lives
  next to the extension.

## 10. Build order

0. **Spike.** A hello-world extension on GitHub Pages calls a `GET /api/table/v1/ping` on a local Codex, from Chrome, Firefox and Safari.
   This shows whether the GM link works locally, and what each browser asks. If it fails, the GM link uses the Funnel address.
   The same spike answers the other unknowns:
   - Does `uploadScenes` keep fog shapes, and does Dynamic Fog make walls from them?
   - Can a scene upload carry token images?
   - How does Dynamic Fog store doors?
   - Does `uploadImages` say where the images ended up?
1. Table API, part 1: link codes, table tokens, auth, CORS, ping, campaign summary, the Owlbear page in Codex.
2. Extension: link and status (§5.1).
3. Tokens to characters, the sheet panel and rolls (§5.2–5.3).
4. Combat, both ways, plus live updates (§5.4).
5. Reading wiki and Place pages, and handouts (first half of §5.5).
6. Player links (§3b).
7. Token art (§5.7).
8. Pins, exploration and scenes (§5.5–5.6), after map pages in Codex.

Steps 1 to 4 can be one PR, or one PR for the API and one for the extension.

## 11. Open questions

1. **The extension's name** in Owlbear's list, and the metadata namespace (`io.github.myarichuk.codex` assumes GitHub Pages).
2. **Licence.** Codex is PolyForm Noncommercial. Should the extension's code use the same licence, or a permissive one so other tools
   can learn from it? Owlbear's store may have its own rules for listed extensions.
3. **Other dice.** Codex owns its own rolls. Should it also try to read rolls from Owlbear's dice extension? It has no documented API,
   so the default is no.
4. **More than one scene.** Link tokens per scene, or once per room?
5. **Doors.** Dynamic Fog stores doors in a way that isn't documented. Either write doors in that format once the spike finds it (it
   could change without notice), or leave doors to its door tool.
6. **Animated dice** in the panel: worth it, or is a clean roll log enough?
