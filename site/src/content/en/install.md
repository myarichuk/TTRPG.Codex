---
title: Install TTRPG.Codex
lead: Download, start, share a link with your players. No account in the cloud, no runtime to install.
description: How to install TTRPG.Codex on Windows, macOS or Linux, let players join from their own devices, install it on a phone, back up your campaigns and run it from source.
---

TTRPG.Codex runs on **your computer**, as the DM. Your players open it in an ordinary web browser, so there is nothing for them to install. Everything below takes about five minutes.

## Choose a download {#download}

Open the [latest release](https://github.com/myarichuk/TTRPG.Codex/releases/latest) and pick the file for your system. Each package already contains the .NET runtime it needs.

| System | Installer | Portable |
| --- | --- | --- |
| Windows (x64) | `TTRPG.Codex-<version>-win-x64-setup.exe`, installs for your user, no admin rights needed | `.zip` |
| macOS (Apple silicon) | `.pkg`, installs to Applications | `.tar.gz` |
| Linux (x64) | `.deb` (Debian, Ubuntu and relatives) | `.tar.gz` |

If you are not sure which to take, take the installer.

> [!NOTE]
> The packages are not code-signed yet, so Windows and macOS will ask for your confirmation the first time. The steps below show where to click.

## Windows {#windows}

1. Run `TTRPG.Codex-<version>-win-x64-setup.exe`.
2. If **Windows protected your PC** appears, choose **More info**, then **Run anyway**.
3. Finish the installer and start **TTRPG.Codex** from the Start menu.

Prefer no installer? Unzip the `.zip` anywhere and run `launcher\ttrpg-codex-tray.exe`.

## macOS {#macos}

1. Open the `.pkg`. If macOS refuses because it is from an unidentified developer, **right-click the file and choose Open**, then confirm.
2. Start **TTRPG.Codex** from Applications. Its icon appears in the menu bar.

Prefer no installer? Extract the `.tar.gz` and run `launcher/ttrpg-codex-tray`.

## Linux {#linux}

Install the package, then start **TTRPG.Codex** from your applications menu:

```sh
sudo apt install ./ttrpg-codex_<version>_amd64.deb
```

The app lives in `/opt/ttrpg-codex`, and a `ttrpg-codex` command is added to your PATH. Prefer a plain archive? Extract the `.tar.gz` and run `launcher/ttrpg-codex-tray`.

> [!TIP]
> GNOME does not show tray icons unless an AppIndicator extension is installed. Without one the launcher still works: it keeps a normal window, which minimizes instead of hiding.

## First start {#first-run}

The first start takes a little longer while the database is prepared. Then:

1. A welcome window asks who will be playing. Choose [[ui:Launcher.Welcome.HostTitle]] to let players on your Wi-Fi join, or [[ui:Launcher.Welcome.SoloTitle]] to keep it to this computer for now. You can change this at any time.
2. Your browser opens the app at `http://localhost:5080`.
3. Create an account. **The first account becomes the server administrator.**
4. Press [[ui:Page.Home.New]], name your campaign and pick a game system.

TTRPG.Codex now lives in your system tray (the menu bar on macOS). Its menu starts and stops the server, copies the link for your players and opens the log. Closing the launcher window does **not** stop the game; use [[ui:Launcher.Quit]] for that.

## Let your players join {#players}

Your computer is not on the internet, so players need a link that reaches it.

### In the same room, or on the same Wi-Fi {#wifi}

1. In the launcher, turn on [[ui:Launcher.Lan.Toggle]]. Your firewall may ask once: allow **private networks**.
2. Click [[ui:Launcher.QrCode]] and let players scan it with their phone camera, or click [[ui:Launcher.CopyLink]] and send it to them.
3. In your campaign, copy the **invite link** from the Overview page and send that as well. Players create an account and they are in.

Keep the computer awake and plugged in while you play: if it sleeps, everyone is disconnected.

### Players somewhere else {#remote}

Players outside your network need a private route to your computer. The easiest free one is [Tailscale](https://tailscale.com):

1. You and each player install Tailscale and sign in.
2. In Tailscale, share your computer with each player.
3. Turn on [[ui:Launcher.Lan.Toggle]]. A link labelled [[ui:Launcher.Link.Tailscale]] appears. Send it to them; they open it in their browser while Tailscale is on.

> [!WARNING]
> Do not forward a port on your router to put TTRPG.Codex on the open internet. It is built for a private table, not for public hosting.

### If a player cannot connect {#cant-join}

Click [[ui:Launcher.CantJoin]] in the launcher. It checks the server, your network and your address, and tells you what to fix, starting with the first red item. The usual causes:

- The firewall blocked it. Allow TTRPG.Codex on private or home networks.
- Guest or café Wi-Fi often stops devices from seeing each other. Use your normal Wi-Fi, or Tailscale.
- Your computer's address changed after a restart or a new network. Copy the new link from the launcher and send it again.
- Network access was switched on after the server started. Stop the server and start it again.

## Install it on a phone {#phone}

The web app is installable, so a player can keep it on their home screen like any other app.

1. Open the link in Safari (iPhone and iPad) or Chrome (Android).
2. Choose **Add to Home Screen**.

It then opens full-screen with its own icon. The live table needs a connection to your computer; when offline you get a plain "you're offline" page instead of stale data.

## Your data, backups and updates {#data}

Campaigns live in a database in your user folder, never inside the program folder, so **updating or reinstalling never touches them**.

| System | Data folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\TTRPG.Codex` |
| macOS | `~/Library/Application Support/TTRPG.Codex` |
| Linux | `~/.local/share/ttrpg-codex` |

The launcher menu opens these folders for you: [[ui:Launcher.Tray.Data]], [[ui:Launcher.Tray.ContentFolder]] and [[ui:Launcher.Tray.BackupsFolder]]. Your own game systems and content packs go in the `content` folder inside the data folder.

> [!WARNING]
> The embedded database's free licence does not allow its built-in scheduled backups, so do not rely on the Backups folder. Use [[ui:Campaign.Settings.Export]] in each campaign's Settings: it downloads the whole campaign (characters, sessions, lore, notes and homebrew) as one file, and [[ui:Campaign.Settings.Import]] restores it.

To update, install the newer package over the old one. To uninstall, use *Apps & features* on Windows, delete the app from Applications on macOS, or run `sudo apt remove ttrpg-codex` on Linux. Your campaigns stay in the data folder until you delete it.

## Run it from source {#source}

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/myarichuk/TTRPG.Codex.git
cd TTRPG.Codex
dotnet run --project src/Codex.Web
```

Open `http://localhost:5183`, create an account and a campaign, and share the invite link. Data lives in an embedded RavenDB under `src/Codex.Web/RavenData`; change it with the `Codex:DataDirectory` setting. Content is loaded from the folders in `Codex:ContentDirectories` in `appsettings.json`. Run the tests with `dotnet test`.

To let players on your network join a source run, bind to all addresses:

```sh
ASPNETCORE_URLS=http://0.0.0.0:5183 dotnet run --project src/Codex.Web
```

## Troubleshooting {#troubleshooting}

**The browser did not open.** Open `http://localhost:5080` yourself, or use [[ui:Launcher.Tray.Open]] from the tray menu.

**The server would not start.** Another program may be using port 5080, often a second copy of TTRPG.Codex. Close it and start again. The launcher's log ([[ui:Launcher.Tray.Log]]) shows the details; send it to whoever is helping you.

**The first start is slow.** That is normal. Later starts are quick.

**Something looks wrong after an update.** Hard-refresh the page (Ctrl+F5, or Cmd+Shift+R on a Mac).

Still stuck? [Open an issue](https://github.com/myarichuk/TTRPG.Codex/issues) and attach the log.
