# TTRPG.Codex

**Modular tabletop RPG campaign and character management system.**

A lightweight digital companion for tabletop play that replaces paper sheets while preserving the tabletop feel.
Supports multiple TTRPG systems via plugins and uses an **Entity Component System (ECS)** runtime to model living character state.

## Architecture Summary
TTRPG.Codex models characters and game state using an **Entity Component System (ECS)** pattern.
It consists of a Blazor Server front-end and a modular plugin engine loaded via reflection.
Data is persisted in an embedded RavenDB instance.

## How to Run
```sh
dotnet restore
dotnet build
dotnet run --project src/Codex.Web
```
The server will start at `http://localhost:5000`.

## How to add plugins
Drop compiled plugin `.dll` files implementing `ICodexSystemPlugin` into the `/plugins` directory. The engine will discover and load them on startup.

## Testing
Run tests using:
```sh
dotnet test
```

## Deployment
Use the scripts in `/scripts` to create single-file executables for Windows, Linux, and MacOS.
