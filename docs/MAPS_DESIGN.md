# Maps

Status: design draft, 2026-11-15. Nothing is built. Open questions are in §11.

Codex has places but no map pictures yet:
- a region's places form a tree (`RegionDocument`, `LocationDocument`, `PlaceTree`);
- each place has a revealed or hidden state, a wiki page and art;
- a feature place has DM notes on its lock, trap, hiding place and contents.

This doc adds maps on top of those places:
- map pages;
- imports from other map tools;
- an editor the GM can drag and drop in;
- the same editing tools for AI over MCP;
- later, generated dungeons and worlds.

The Owlbear extension (docs/OWLBEAR_EXTENSION_DESIGN.md) and `.uvtt` export send these maps to the table.

## 1. Principles

1. **A map belongs to a place.** The world map belongs to the region, a city map to the settlement, a dungeon level to its section.
   Pins and areas on a map are that place's child places. Clicking a pin with its own map opens that map, so maps nest the way places
   already do.
2. **The place is the source of truth.** A pin is a place's position, not a copy of it. Name, wiki page, visibility, DM notes and
   creatures stay on the place. Revealing a place reveals its pin and area. Deleting a pin asks whether to delete the place too.
3. **Maps are data first and pictures second.** Walls, doors, lights, objects and links are stored as shapes with coordinates, and
   the picture is drawn from them. So walls are always known exactly, and every export (`.uvtt`, Owlbear, a PNG) comes from the same
   data. An uploaded picture with nothing traced on it is a map too, just one with less data.
4. **One set of changes for everyone.** The editor, imports, the dungeon generator and AI all make the same changes
   (§3). Undo, permission checks and validation live in one place.

## 2. The map

A `MapDocument` per map, stored apart from the region so a big map doesn't weigh down loading places.

- **Owner**: the place (or region) it belongs to. A place can have **several maps**, its *versions*: summer and winter, before and
  after the siege, the forest before and after the wildfire, each floor of a tower, a handout. Each has a label, and one is the place's
  *current* map, the one its pin opens (§9.2).
- **Size and grid**:
  - square, hex (either way) or none;
  - pixels per cell;
  - the size of one cell in the system's units, for example 5 ft;
  - the grid's offset on the background image.
- **Layers**, bottom to top. Every element has an id and coordinates in map pixels.
  1. *Background*: uploaded images, if any.
  2. *Floor*: rooms as cell sets or polygons, each filled with a tiling texture.
  3. *Walls*: lines with a kind (wall, door, secret door, window, low wall) and a style (stone, wood, cave). Doors are open or closed.
  4. *Decals*: brushes painted for looks only (cracks, moss, blood, rubble, rugs). Placed freely, off the grid if the GM likes.
     They have no links and mean nothing to the rules, so there can be thousands.
  5. *Features*: brushes that mean something (§5): a chest, a statue, an altar, a pillar that blocks sight. Each can block sight or
     movement, give light, and link to things.
  6. *Lights*: position, range, colour, primary or secondary.
  7. *Links*:
     - pins and areas, each pointing at a place;
     - a feature can point at a place, a wiki page, a library entry or a creature on the cast.
  8. *Labels*: text on the map.
  9. *GM layer*: notes, trap areas and secret doors drawn for the GM only.
- **Where things are** (the map's spatial data): on every save the server works out which room or area each feature, light and pin
  sits in, and which rooms each door joins. So "what's in room 4", "what's behind this door" and "what can be seen from here" are
  quick questions for exploration, AI tools (§6) and exports, without reading pixels.
- **Player view**: drawn from the same data with GM-only things removed. Places the party hasn't found are covered or left out.
  The server builds it, so a player's browser never receives a secret.

## 3. Changes

Every edit is a small change:
- *add room*, *add wall*, *set door*;
- *place feature*, *scatter decals*, *move*, *rotate*;
- *add version*, *make current*, *add hint*;
- *link to place*;
- *remove*.

Changes come in batches. The server checks each batch (in bounds, a real place in this campaign, the GM's own map) and stores it.
Then it tells everyone else who has the map open (a co-GM, players, the AI chat's view) through live updates, below.

The same changes are:
- the editor's undo history;
- what imports produce;
- what the dungeon generator produces;
- what AI tools call (§6).

### Live updates

**What happens today** (read 2026-11-15):
- Every open Codex page already keeps a live connection to the server. That's how Blazor Server works: clicks go up it and screen
  changes come back down. So the server can update a page at any moment, without the page asking.
- What's missing is a way for one person's save to reach *other* people's pages. Today the only path is `CampaignRuntime`, the
  table's state, which exists only while a session is running. It raises `StateChanged` with the ids of the actors a command
  touched, and pages like the combat console (`CombatConsole.razor`) listen and redraw.
- So during a session, the table updates live for everyone. Outside a session, a GM revealing a place or editing a wiki page is seen
  by others only when they reload.

**What maps need:**
- An always-on **campaign notifier**: one small object per campaign, alive whenever anyone has the campaign open, not only during a
  session. A save raises "map M changed (version 12)", "place P revealed", "wiki page W changed". Pages listen for what they show.
- Each page then reloads only what it shows, *through the same filtered repository calls as a fresh load*. The message itself
  carries no content, just what changed, so a player never receives a secret through it.
- The map editor's canvas is browser code. The map page passes each change batch to it through the page's existing connection
  (JS interop), and the canvas animates it in (§6). No second connection is needed.
- It runs inside one Codex process, which is how Codex runs (locally or on one host). Several servers behind a load balancer would
  need a shared message bus. Not needed now.
- It's not a map feature. The Places list, the wiki and the cast need it too, so it's built once and maps are its first user
  (§9.10 step 6).

## 4. Imports

Research on 2026-11-15. Field names marked *check* need real sample files before building.

| Source | What it gives | Useful? | Feasible? |
|---|---|---|---|
| **Any picture**: Inkarnate, Wonderdraft, Dungeon Painter, a scan, a published adventure's map the GM owns | The image only | Yes. It's how most GMs have maps today. | Easy. The GM sets the grid, which Codex can usually find on its own (§8), and places pins. |
| **Universal VTT** (`.uvtt`, `.dd2vtt`, `.df2vtt`): Dungeondraft, DungeonFog, Arkenforge and others | One JSON file: the image (base64), grid size in pixels, walls, doors ("portals"), lights, and outlines of objects that block light | Yes. Real walls and doors, and Roll20, Foundry and others read it too. | Easy. Field names are known only in outline (*check*). Also the export format (§7). |
| **Azgaar's Fantasy Map Generator**: full JSON and GeoJSON exports | Burgs (settlements), states, provinces, markers with notes, routes, rivers | Yes, for worlds. Burgs become settlements, states and provinces become areas, markers become pins with a wiki page. | Medium. The full JSON is large. The picture is a separate SVG or PNG export. Reports of its licence conflict (MIT is likely, *check*), but we only read the user's own file. |
| **Watabou's One Page Dungeon**: JSON export | Rooms, doors, columns, water, notes, and the image in base64 | Yes. Each room becomes a child place, with its note as the description. | Easy to medium. The schema isn't documented (*check* with samples; notes sit under `ref`). Watabou's city and village generators export too (*check*). |
| **Foundry scene** (the GM's own export) | Walls, doors, lights, and journal notes on the map | Medium: GMs moving from Foundry. Notes could link to imported wiki pages. | Medium. The image is a separate file, and the layout differs between Foundry versions. Later. |
| **Kanka maps** | A map with markers linked to its entries, through Kanka's API | Medium: GMs moving their whole wiki. It belongs with a Kanka wiki import, not on its own. | Large. Later, as part of that import. |
| Owlbear scenes, Tiled, LDtk, Dungeondraft's own files | Varies | Low | Owlbear's format isn't published. Tiled and LDtk are rare at RPG tables. Dungeondraft's own files aren't documented, and its `.dd2vtt` covers it. Skip. |

Every import shows what it found ("12 rooms, 9 doors, 31 notes") and a preview, so the GM can untick rooms that shouldn't become places.
Then it runs as one batch of changes, which one undo removes.

## 5. The editor

### Brushes

Brushes as PNGs with transparency work. Dungeondraft and most battle-map tools work this way. Each brush is one image plus:
- **Size in cells** (1×1, 2×1, 3×3) and an anchor point, so it snaps to the grid. Images are scaled to the map's pixels per cell.
  Brushes drawn at 256 px per cell look sharp at the usual export sizes.
- **Rotation**: free, or snapped to 90° or 45°.
- **Tags**: furniture, nature, dungeon dressing, and so on, plus the themes it suits.
- **Blocks**: sight, movement, both or neither. A blocking brush gets an outline traced from its transparent pixels when it's added,
  which the GM can fix by hand. That outline is what `.uvtt` and Owlbear get as an object that blocks sight.
- **Light**: an optional light it gives off (a torch, a brazier).
- **Variants**: several images to pick from at random, so a row of crates doesn't look stamped.
- **Licence and credit**, shown in the brush library and in exports that need it.

Not everything should be a brush:
- **Floors** are rooms filled with a tiling texture. A big hall would need hundreds of floor stamps.
- **Walls** are lines drawn with a wall style. That keeps them exact data for line of sight, doors and exports.
- **Roads and rivers** on world maps are lines too.

### Using it

- **Rooms**: drag a rectangle or paint cells, and walls follow the room's outline on their own, as in Dungeon Scrawl. Joining two
  rooms opens the wall between them.
- **Doors**: click a wall to make a door. Choose its kind (door, secret door, portcullis) and whether it starts open.
- **Decals**: pick a decal brush and paint. Click to stamp, or drag to scatter along the stroke with random rotation, size and
  variant, the way Inkarnate scatters trees. Density and spread are sliders.
- **Features**: drag a brush from the library and drop it on the map. Snap to the grid, rotate, make a copy, choose a variant.
- **Links**:
  - drop a pin, draw an area, or pick any feature, then *Link*;
  - choose an existing place, or make a new child place right there;
  - a feature linked to a feature place (a chest, a statue) carries that place's lock, trap and hiding notes;
  - a feature linked to a creature on the cast becomes that creature's starting token in exports.

### Context: what the editor knows

The editor knows where it is. A map belongs to a place, so it knows the place's kind (a dungeon section, a tavern, a forest), the
region, the campaign's system and the map's theme. It uses that:

- **The brush library** puts brushes tagged for this theme and room first. A crypt offers sarcophagi before market stalls.
- **The *Add* panel** has tabs beside the brushes:
  - **Places**: the place's child places not on the map yet. Drag one on to place it.
  - **Cast**: creatures and people tied to this place. Drag one on to set where it starts.
  - **Library**: entries from the campaign's packs and homebrew, searched the way the world builder chat searches
    (`search_library`), with this place's tags first. Dragging a monster adds it to the cast (`CastService.AddToCastAsync`, as the
    Library and Bestiary pages do) and places it. Dragging an item onto a container is below.
  - **Wiki**: pages to link a feature to, such as a shrine to its god's page.
- **New homebrew in place.** When nothing fits, *New…* opens the existing homebrew editor (`HomebrewWizard`) with the type
  already chosen, and drops the saved entry where it was asked for. Not a second editor.

### Containers

A chest, a desk or a hollow statue is a feature linked to a **container place**: a place with `PlaceFeature.Container` set, as places
already have.
- Its lock, trap, hiding notes and contents live on the place, so the Places page, the map and the table show the same thing.
- Dropping a library item onto it adds a line to its contents with a link to the entry (`[[item:type/id]]`, the mention the wiki
  already uses). Contents stay text, as the design of `PlaceFeature` intends: who takes what is settled at the table, not tracked as
  stock. The contents field gains rendering for those links.
- A plain crate stays a feature with no place. Only things the GM links become places, so the places list doesn't fill up with barrels.
- Opening a container in exploration mode, at the table, shows its contents to the GM to hand out.

### The first version

The wishlist above is large. The editor's first version (M4) is:
- square grid only;
- rooms, walls and doors;
- decals and features from the GM's own uploaded PNGs, with stamp and scatter;
- links to places, cast and library, and containers;
- the list panel and keyboard placement;
- undo.

Brush libraries, AI tools and the live AI chat come after (M5, M6). Hex grids and world-map painting wait until someone asks.
- **Panels**:
  - the brush library;
  - a list of everything on the map (name, cell, link), where any field can be edited by keyboard;
  - the selected element's settings.
- **Views**: *Edit*, *Player view* and *Exploration* (reveal places as the party goes, the same as in Owlbear).

### Is it worth it? Yes, because of the links

A map that stores its walls, objects and the places they link to gives Codex things a picture can't:
- exports with real walls;
- exploration that reveals rooms;
- traps and creatures where they actually are;
- AI that can edit maps reliably (§6).

To keep it small: dungeons, buildings and battle maps first (square grid, rooms, walls, brushes). World maps start as an uploaded or
imported picture with pins and areas. Painting terrain for world maps comes much later, if ever: Azgaar and Inkarnate already do it
well, and importing from them is enough.

### How it's built

- The editor is a TypeScript canvas inside the Blazor page, drawing with WebGL (PixiJS or similar).
  Dragging and zooming happen in the browser. Each round trip to the server carries only a batch of changes. Doing this in Blazor
  Server alone would lag on every mouse move.
- The server keeps the map and draws pictures for exports, previews and the player view, with SkiaSharp or similar. The two drawing
  paths use the same simple rules: textured polygons, stroked lines, rotated images. Tests compare their output on sample maps, so
  they don't drift.
- Your RoguelikeToolkit.World map editor (React, WebGL2) is for hex worlds. Its drawing and paint code is a reference, not a part.

### Accessibility

- Everything placed with a mouse can also be placed by keyboard: pick a brush, then move it with the arrow keys one cell at a time,
  rotate with R, and confirm.
- The list of everything on the map (above) is a full text view of it, for screen readers and for edits that are fiddly by hand.
- Colour is never the only signal: doors and secret doors differ in shape too.
- Players get the list of places they've found as well as the picture.

## 6. AI over MCP

The world builder's phase 2 already plans its tools over MCP (WORLD_BUILDER.md §7). Map tools join them, and the in-app chat uses the
same tools. AI edits a map through the same changes as the editor (§3), never by painting pixels. That's why it can be reliable.

- `list_brushes` (by tag or theme), `get_map` (a text summary from the map's spatial data: rooms, doors, features and what's in each
  room, links), `render_map` (a small PNG, for models that can see).
- `create_map`, `add_room`, `connect_rooms` (with a door kind), `place_brush`, `scatter_decals` (a brush, an area, a density),
  `add_light`, `link_to_place`, `create_place_here`, `copy_map_as_version` ("the same keep, after the siege").
- The world builder's own tools stay available alongside: `search_library`, `list_places`, `get_page`, and `propose_changes` for
  the places, people and homebrew a map needs.
- Like world-builder proposals, AI changes arrive as a **draft**. The GM sees it on the map, then keeps it, edits it or throws it
  away. Nothing is saved until they do.

This uses the GM's own AI provider and key. A good test: "add a collapsed side passage with a hidden chest to room 4" should produce a
room, a secret door, a chest brush and a linked container place with its notes.

### Chatting while watching the map

The editor gets a chat panel beside the canvas: the world builder chat (`WorldChat`), opened with this map as its subject, not a
second chat.
- **Each tool call adds to the draft as it happens.** The GM watches the room appear, then its door, then the chest, while the
  model is still working, and can type "no, put it on the north side" mid-way.
- **The draft is kept on the server** with the chat, as world-builder proposals are, so a reload or a dropped connection doesn't
  lose it. The canvas gets each step through the page's connection (§3).
- **Draft things look like drafts**: a dashed outline and a soft glow, so the GM never mistakes them for saved ones.
  *Keep all*, *Keep this* and *Discard* are on the panel and on each drafted thing.
- **Things pop to life**:
  - rooms draw their floor outward from the centre;
  - walls trace along their length;
  - features drop in with a small bounce and a dust puff;
  - decals fade in along the scatter stroke;
  - lights fade up.
  Each takes about a third of a second and runs in the browser. A batch of fifty things is staggered, not played one by one.
- **Reduced motion**: when the browser asks for less motion, things appear at once with only a short fade.
- **Screen readers**: the list panel is a polite live region, so each addition is read out ("Added: chest, room 4, draft").
- The same draft and animation come from imports and the dungeon generator, so a generated dungeon assembling itself is the same
  code, not extra work.

## 7. Exports

- **Picture**: PNG or WebP at the chosen pixels per cell, with or without the grid, as the GM's view or the player view.
- **`.uvtt`**: picture, grid, walls, doors, lights and sight-blocking objects.
- **Owlbear scene bundle**: see the Owlbear doc §5.6.
- **Foundry scene**: later, alongside the Foundry module.

## 8. Reading uploaded pictures

- **Finding the grid**: feasible and worth doing. Grid lines repeat at a fixed spacing, so the spacing and offset can be found from the
  image. The GM confirms or adjusts it.
- **Finding walls in a painted map**: only reliable on clean line art, and fixing wrong walls takes longer than tracing them. Not in
  the first versions. *Trace walls* in the editor is the answer, maybe with a "suggest walls" helper later.
- **Reading place names off a world map**: a vision model on the GM's own key could suggest pins from the names printed on an
  uploaded map. Untested. Later.

## 9. Map pages (M1) in detail

M1 is the smallest useful piece: put a picture on a place, mark where its places are, and show players what they've found. No editor,
walls or brushes yet. Everything later builds on this document and these rules.

### 9.1 What's there now (read 2026-11-15)

- **Places** are `LocationDocument`s inside one `RegionDocument` per region (CAMPAIGN_ARCHITECTURE.md §2). The tree comes from
  `ParentId`. Each place has a `Kind`, a `Visibility`, a `WikiPageId`, DM-only `Feature` notes and art.
- **Players** get only places whose `Visibility` is `Known`. `RavenRegionRepository` filters before anything reaches a page, and strips
  `Feature`. *Reveal* (`SetLocationVisibilityAsync`) is DM only.
- **Art** is stored as RavenDB attachments on the owning document (`MediaStore`). Its limit is 6 MB, JPEG, PNG and WebP. SVG is refused,
  since it can carry scripts. `MediaEndpoints` serves each image after checking campaign access.
- **The Places page** is the `world` section (`WorldSection.razor`): a list per region, with reveal, edit and *Open page*.
- **Live updates** go through `CampaignRuntime` (one per campaign, used by the table and combat), which only exists while a session is
  running. The Places page doesn't refresh itself when the GM reveals something.
- **SkiaSharp** is already a dependency.

### 9.2 The map document

```
MapDocument
  Id, CampaignId
  RegionId                 the region it belongs to
  LocationId               the place it shows; null for the region's own map
  Name                     defaults to the place's name
  Label                    which version this is: "Summer", "After the fire", "Second floor"; empty for a place's only map
  Current                  whether this is the version the place's pin opens
  ShownToPlayers           whether players may see this version at all (see 9.4)
  Image                    attachment "map": width, height, version
  PlayerImage              optional attachment "map-player" (see 9.4)
  Grid                     kind (none, square, hex rows, hex columns), cell size in pixels,
                           offset, cell size in game units ("5 ft"), shown or not
  Markers[]                Id, LocationId, Shape (pin: a point | area: a polygon), label position,
                           Hint (optional, see 9.4)
  Version, UpdatedAt
```

- **A place can have several maps, its versions**, and so can the region:
  - Seasons, a siege's stages, a burnt forest, a flooded crypt, each floor of a tower, a handout.
  - One version is **current**, chosen by the GM: the place's pin opens it, and the map page shows a version switcher
    ("Summer · Winter · After the fire").
  - **Copy as new version** copies the picture, grid and markers. The GM then uploads the changed picture or, once the editor exists,
    edits the copy (burn the roofs, break the walls). That's the cheap way to make a ruined state.
  - Markers belong to one version but point at the same places. Revealing a place shows it on every version. A ruined version can
    leave out a building that's gone, or mark it with an area of its own.
  - **Shown to players** is off for a version the party mustn't see yet, such as the town after the fire before the fire happens.
    Players get only versions that are shown and, of those, the current one first.
  - **New maps and copies start hidden from players.** Players see the whole picture of a shown map (there are no covered parts yet,
    9.4), so uploading a GM's picture must never publish it. Show to players is the GM's own step, and the page says what it shows.
    A map with no picture is never offered to players, by its pin, its Map button or the version list.
  - **Add map on a place that already has a map with no picture opens that map** instead of making another, so pressing it twice
    makes one map. A guard document per owner (`MapOwnerGuards/{campaign}/{place or region}`), saved with each new map under optimistic
    concurrency, keeps two Add map presses at once from making two maps, or two current ones.
  - Switching the current version is the GM's move at the table: "the siege begins" switches the map for everyone (live, §3).
- **Coordinates are in the image's own pixels.** A replacement image of a different size asks the GM to keep markers in proportion or
  leave them where they are.
- **Markers never copy a place's name, text or visibility.** They hold only its id. A marker whose place is gone is dropped on the next save.
- **Its own document, not part of `RegionDocument`.** A map's image and markers change on their own, and loading the places list
  shouldn't load maps. A region can find its maps with a small index on `RegionId`.
- **Moving a place to another region** (`MoveLocationAsync`) moves its markers off the old region's maps. The GM places it again on the new one.

### 9.3 Images

- JPEG, PNG and WebP, as now. Never SVG.
- **Map images get their own limit**: 25 MB and 67 megapixels, Owlbear's free plan limits, so anything Codex accepts can go on to
  Owlbear unchanged. Portraits keep 6 MB.
- On upload the server:
  - reads the size;
  - checks the pixel count before decoding, so a crafted file can't use up memory;
  - makes a smaller copy for the map list.
- Map images are served like art, after the same campaign access check. A player can load a map image only if the version is shown to
  players, and the map's own place is `Known` to them, or it's the region's map and the region has at least one place they know.

### 9.4 What players see

The rule is the same as for places today: **nothing hidden reaches a player's browser.**

- **Markers**: only those whose place is `Known` to this player. A hidden place's pin or area is never sent, not even its shape.
- **The picture**: many maps show things players shouldn't see yet. A hidden room's shape, a secret door marked *S*, the GM's numbers.
  The GM chooses one of three when they upload:
  1. **Show the whole picture.** Fine for world maps and anything without spoilers.
  2. **Cover what isn't found.**
     - The server draws the player picture with SkiaSharp, painting over each area whose place is still hidden.
     - Only areas get covered. A hidden pin's spot stays visible, so the GM draws an area for anything that must be covered.
     - The result is cached per map version and set of revealed places.
     - The player's browser only ever gets the covered picture.
  3. **A separate player picture.** Many map tools export a GM version and a player version. The GM uploads both, and players get
     the player one, covered as in 2 if the GM wants.
- **The grid** is drawn by the browser over the picture, so turning it off doesn't need a new image.

**"Something is there": hints.** The party hears water dripping to the north, sees a shape in the fog, smells smoke from the woods.
The place stays hidden, but the GM can let players know *something* is there.
- A hint belongs to a marker, not to the place, and the GM writes it: what the party senses ("Dripping water, somewhere north"), and
  optionally how (heard, seen, smelled, rumoured), which sets its icon.
- Its position is **deliberately rough**: a soft blurred circle the GM sizes, not the true pin or area. A hint can sit off to one
  side of the real place, since sounds carry.
- **Players get only the hint**: its text, icon and rough circle, under an id made for the hint. Never the place's name,
  description, id or true shape. The server builds hints apart from places, so they can't be joined to the place list.
- The covered picture (option 2) stays covered under a hint.
- Revealing the place replaces the hint with the real marker. The GM can also remove a hint, or add one at the table as the party
  hears it (live, §3).
- Places keep their two states, Hidden and Known. The place editor and reveal only ever set those two; `Unknown` is used by actors alone (the stranger). A hint is a
  separate setting on top of Hidden, so no place that exists today starts showing a hint on its own.

### 9.5 The page

- **Where it lives:**
  - A *Map* tab on the Places page (`world`), next to the list.
  - On each place's row, a *Map* button when it has one, and *Add map* for the GM.
  - URL: `/campaigns/{id}/world/map/{mapId}`, so a map can be linked from the wiki and the book.
- **GM view:**
  - The picture, with zoom and pan.
  - Every marker. Hidden places are drawn with a dashed outline and the *Hidden* label the list uses.
  - A side panel of the map's places, made from the tree under the map's place:
    - places on the map, each with *Reveal*, *Edit* and *Open page*;
    - places not on the map yet, each with *Place on map*.
  - *Place on map*: click for a pin, or click corners for an area, then Enter. *Add a place here* makes a new child place where you
    clicked. It asks for a name, then opens the usual place editor.
  - Selecting a marker shows the place's name, kind, description, wiki link and, for a feature, its DM notes. Same text as the list.
  - **Nested maps**: a pin whose place has its own map shows a small "map" sign. *Open map* goes down a level, to that place's
    current version, and the breadcrumb (region › area › city › tavern) goes back up.
  - **Versions**: a switcher over the map lists the place's versions. The GM can make one current, copy it as a new version, rename it,
    or hide it from players.
  - **Hints**: on a hidden place's marker, *Add hint* asks what the party senses and lets the GM drag and size the rough circle.
- **Player view:**
  - The picture as in 9.4, the pins and areas they've found, and the same side panel listing only what they know.
  - Selecting a marker shows the place's public description and *Open page*.
  - The GM can switch the whole page to *Show as a player*, like the existing preview (`PreviewEndpoints`).
- **Reveal from the map**: the same `SetLocationVisibilityAsync` as the list. The marker shows at once for the GM.
  - Players with the map open see the new pin, and the covered area clears, without reloading, in a session or not. That's the
    campaign notifier (§3). Until it's built (step 6), players see a reveal the next time they open the map.
- **Grid setup**: *Set grid* shows the picture with a grid laid over it.
  - The GM drags to match the cell size and offset, or clicks *Find grid* (§8). It fills both in, and the GM adjusts if needed.
  - Fields for the cell size in pixels and in game units allow exact typing.
  - A grid is optional: world maps usually have none.

### 9.6 Accessibility

- The side panel is the map's text version. Everything the picture shows (which places, where, what is hidden) is there as a list,
  reachable by keyboard and screen reader. Selecting in one selects in the other.
- **Placing by keyboard:**
  1. Choose a place in the panel, then *Place on map*.
  2. Move a cross-hair with the arrow keys: one grid cell, or 1% of the image without a grid. Shift moves ten times as far.
  3. Enter drops a pin. For an area, Space adds a corner and Enter finishes it.
- **Labels for screen readers**: each marker's is its place's name and state ("Old Mill, hidden"). The picture's alt text is the map's
  name and its place.
- Zoom and pan work with + and − and the arrow keys as well as the mouse wheel and pinch.
- Hidden markers differ from found ones by outline style and label, not only by colour.
- Right-to-left: the side panel and toolbar follow the page's direction. The picture itself is never mirrored.

### 9.7 Wiki and the book

- A wiki page can show a map with a stored mention, `[[map:id]]`, like the existing `[[place:id]]` (`WikiText`), drawn as a thumbnail that opens the map
  page. Players see the thumbnail only if they may see the map.
- A place's wiki page gets a small *On the map* link when it has a marker, which opens the map at that marker.
- The book export can include player-view maps later (§11).

### 9.8 Campaign export, backup and deleting

- `CampaignExportService` gains a `maps` list with grids and markers. Today's export leaves images out (portraits, place art, pack
  art), so map images stay out too, and the map comes back as "no picture yet". A map is worth little without its picture, though.
  Adding images to the export would help every kind of art, so it's a separate change, not part of M1.
- The daily server backup (`BackupService`, `JsonBackups`) writes each document's attachments too, so map images are backed up from
  day one.
- Deleting a place drops its markers. Its own map goes with it, after the usual undo period, like its art (`RemovedArt`).
- Deleting a region deletes its maps.

### 9.9 Tests

- **Repository**:
  - a player never gets a marker for a hidden place, from any call;
  - a player can't load a map image for a place they don't know;
  - a feature's notes never leave the GM's view;
  - a version not shown to players never reaches them, as a map or an image;
  - a hint reaches players with its text and rough circle only: no place id, name, description or true shape.
- **Covered picture**: draw a map with one hidden area and check its pixels are painted over. Reveal it and check the cache gives a new
  picture.
- **Images**: an oversized image, a pixel bomb and an SVG renamed to .png are all refused.
- **Page (E2E)**:
  - GM uploads a map, sets the grid, places a pin and an area by mouse and by keyboard, reveals one;
  - a player in a second browser sees only the revealed one, during a session without reloading.

### 9.10 Steps

1. `MapDocument` with versions, its repository with player filtering, map images and their limits. Tests.
2. The map page: GM view, side panel, pins and areas, reveal, the version switcher. Player view showing the whole picture.
3. Covered pictures and separate player pictures.
4. Grid setup and *Find grid*.
5. Nested maps and the breadcrumb. Wiki map links and *On the map*.
6. The campaign notifier (§3): live reveal, version switches and hints for everyone with the map open, in a session or not. The
   Places list uses it too.
7. Hints.
8. Campaign export and deleting.

Steps 1 and 2 are one PR. 3 to 8 can each be a small PR, in any order, except that hints are most useful after step 6.

## 10. Build order

- **M1. Map pages.**
  - Upload a picture and set the grid, with grid finding.
  - Pins and areas linked to places, and nested maps.
  - Versions of a map (seasons, ruin, floors).
  - Player view by place visibility, and hints.
  - Exploration reveal in Codex itself, live through the campaign notifier.
- **M2. Universal VTT** import and export. Owlbear scene bundle.
- **M3. Imports**: Watabou's One Page Dungeon, then Azgaar.
- **M4. Editor**: the first version in §5 (rooms, walls, doors, decals and features from uploads, links, containers, the
  context-aware *Add* panel, the list panel, keyboard placement).
- **M5. Brush library**: a starter set under a licence that allows it (§11), themes, AI-made brushes.
- **M6. AI map tools** in the world builder chat, with the chat panel in the editor, live drafts and animations (§6). Then the same
  tools over MCP.
- **M7. Dungeon generator.**
  - Themes in YAML: floor and wall styles, brushes per room kind, hazard tables, creature tags.
  - It emits changes (§3), so its dungeons are editable straight away.
  - Traps come from hazards track H.
- **M8. World generation** from RoguelikeToolkit.World: regions, sites and settlements become places on a generated world map.

M1 and M2 are what the Owlbear extension needs to send maps. Everything after M2 can come in any order.

## 11. Open questions

1. **Starter brushes.** Their licence must allow shipping inside Codex. Options: CC0 packs (Kenney's art is CC0, but which packs suit
   top-down battle maps needs checking), brushes made for Codex, or AI-made ones. Most popular free battle-map packs have licences that
   don't allow bundling.
2. **AI-made brushes.** Image models rarely give clean transparency. Prompt for a flat background and cut it out, or run a
   background-removal model? Worth a small test with the image presets Codex already has (`AiImageGenerator`).
3. **Hex grids in the editor**: in the first version, or squares only until someone asks?
4. **Maps in the book export**: should a player-view map print in the campaign book?
5. **Map size limits**: M1 takes 25 MB and 67 megapixels (§9.3). The editor still needs a limit on objects per map. Decals can run
   to thousands, so it needs a test of how many a phone can draw.
6. **Decided 2026-11-15**: several maps per place from the start (versions, §9.2); "something is there" as GM-written hints
   (§9.4); live updates through one campaign notifier that maps use first (§3).
7. **Animations**: drawn by the canvas library (PixiJS or similar), or a small set of hand-written tweens? Decide with the editor's
   first spike.
