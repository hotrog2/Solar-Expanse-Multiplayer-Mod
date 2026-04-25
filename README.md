# Solar Expanse Multiplayer Mod

This repository contains the current **host/client multiplayer mod** for Solar Expanse.

## Current Design Goals

- One player hosts a session.
- Other players join by **direct connect** using the host's public IP and port.
- Lobby players load into the same game when the host starts the session.
- The **host is authoritative for simulation time**.
- Each player is assigned a **company slot** and keeps their own:
  - money
  - resources
  - research
  - missions
  - construction state
- Anything a player builds belongs only to that player's company and is **not merged into other players' companies**.
- Other players can see completed buildings/facilities, but cannot see another player's queued or currently building items.
- The world clock and shared simulation state are intended to be synchronized.

## What Is Implemented

- BepInEx/Harmony plugin structure
- direct-connect TCP host/client session layer
- JSON message protocol
- lobby host/join/start flow from the main menu
- company-slot ownership model
- host-authoritative **time sync** pipeline wired to `Manager.TimeController`
- first-pass public **company-state snapshot exchange**
- first-pass **per-company inventory synchronization** for owned object resource lists and construction equipment counts
- first-pass **per-company completed facility synchronization**
- private command channel for company actions such as production/research requests
- optional **storyline suppression** for contracts, tutorials, and cyclical mission prompts
- simple IMGUI debug/control window for host/join/disconnect

## Ownership Model

- Company state is synchronized **per company slot**.
- Inventories are applied only to the owning company's `ObjectInfoData` entries.
- Completed facilities are applied only to the owning company's `ObjectInfoData` entries.
- Host normalization prevents clients from claiming a different company slot than the one assigned by the host.
- This means a player's built assets are treated as that player's company assets, not as shared/global company assets.
- Private state is not exposed in public snapshots: money, resources, research, construction queues, and current construction are kept per player/company.

## What Is Not Finished Yet

This is still an experimental multiplayer build. The following systems still need deeper gameplay replication work:

- deeper resource synchronization beyond current owned inventory snapshots
- complete production/research action coverage
- mission replication
- shared object/world delta replication
- join-in-progress world snapshotting
- desync detection and recovery
- permissions/validation for remote actions
- authoritative active-research queue/progress reconstruction

## Sandbox / Storyline Suppression

The multiplayer config supports disabling default storyline-style systems for sandbox play:

- contracts
- tutorials
- cyclical mission prompts

Config key:

- `Gameplay.DisableStorylineSystems = true`

## Expected Environment

- Game: Solar Expanse Unity Mono build (`2022.3.22f1` observed)
- Intended loader: **BepInEx 5.x**
- Intended patch library: **Harmony / HarmonyX**

## Install

1. Install BepInEx into Solar Expanse.
2. Copy the release zip contents into the Solar Expanse game folder.
3. Confirm `SolarExpanse.Multiplayer.dll` is under `BepInEx/plugins/`.
4. Launch the game and use the multiplayer panel from the main menu.

## Build Outline

1. Install BepInEx into a copy of the game.
2. Restore/build this project.
3. Copy the release zip contents into the Solar Expanse game folder so `SolarExpanse.Multiplayer.dll` lands under `BepInEx/plugins/`.
4. Launch the game and use the multiplayer panel from the main menu.

### Notes About Package Restore

The project file includes an additional NuGet source for BepInEx packages:

- `https://nuget.bepinex.dev/v3/index.json`

The project uses:

- `BepInEx.BaseLib` `5.4.21`
- `HarmonyX` `2.10.2`

## Source Layout

- `src/SolarExpanse.Multiplayer/` - plugin source

## First Recommended Next Milestones

1. Validate a full two-machine host/join/start flow.
2. Expand private action handling for build/research/production commands.
3. Add mission replication.
4. Add desync detection and recovery.
