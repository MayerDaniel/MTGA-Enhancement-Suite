# MTGA Enhancement Suite

A [BepInEx 5](https://github.com/BepInEx/BepInEx) mod for **Magic: The Gathering Arena** (Windows) that adds community features Wizards of the Coast hasn't built into the game.

> **Beta** — This mod is in active development. Expect rough edges.

## Installation (Windows)

### One-Line Install

Open **PowerShell**, **CMD**, or **Windows Terminal** and run:

```
powershell -c "irm https://raw.githubusercontent.com/MayerDaniel/MTGA-Enhancement-Suite/main/install.ps1 | iex"
```

This will:
1. Find your MTGA installation
2. Install BepInEx 5 (if not already present)
3. Download the latest plugin release
4. Register the `mtgaes://` URL scheme for invite links

### Manual Install

1. Download [BepInEx 5 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2) and extract it into your MTGA directory (`C:\Program Files\Wizards of the Coast\MTGA\`)
2. Download `MTGAEnhancementSuite.dll` and `config.json` from the [latest release](https://github.com/MayerDaniel/MTGA-Enhancement-Suite/releases/latest)
3. Place both files in `<MTGA>\BepInEx\plugins\MTGAEnhancementSuite\`
4. Launch MTGA normally

## Features

### Custom Format Lobbies
Create challenge lobbies with format rules (currently **Pauper** and **Planar Standard** ). When a player selects a deck, it is validated against the format's legal card list — illegal decks are rejected with a message showing which cards aren't allowed. Format rules are synced between host and joiner in real-time via Firebase, and backed by scryfall.

### Lobby Invite Links
Share a clickable `https://` link to your lobby. The recipient's browser opens a landing page that launches MTGA and joins the lobby automatically — no friend request needed. If MTGA is already running, the link is forwarded to the existing instance.

### Public Lobby Browser
Mark your lobby as public and it appears in a server browser visible to all Enhancement Suite users. Browse open lobbies, see the host name and format, and join with one click.

### Format Sync Across Matches
When a lobby host changes the format, the joiner's client updates automatically. Format settings persist across rematches — play multiple games in the same lobby without re-configuring.

## Usage

1. Launch MTGA — you should see an **MTGA-ES** button in the top navigation bar
2. Create a challenge lobby (Play > Challenge)
3. Use the **format selector** below the coin flip spinner to choose a format
4. Click **Copy Link** to get a shareable invite, or **Make Public** to list in the server browser
5. Click the **MTGA-ES** button to browse public lobbies

## Uninstalling

Delete the plugin folder:
```
<MTGA>\BepInEx\plugins\MTGAEnhancementSuite\
```

To fully remove BepInEx, also delete `winhttp.dll`, `doorstop_config.ini`, and the `BepInEx\` folder from your MTGA directory.

## Building from Source

Requires .NET Framework 4.7.2+ and the MTGA managed assemblies as references.

```bash
dotnet build Plugin/MTGAEnhancementSuite.csproj
```

The build output goes to `Plugin/bin/Debug/MTGAEnhancementSuite.dll`.

## Architecture

| Layer | Technology |
|-------|-----------|
| Mod framework | BepInEx 5 (Mono) |
| Patching | HarmonyX |
| Backend | Firebase Realtime Database + Cloud Functions |
| Card validation | Scryfall API (daily sync to Firebase) |
| URL handling | Custom `mtgaes://` scheme + TCP IPC |

## License

MIT
