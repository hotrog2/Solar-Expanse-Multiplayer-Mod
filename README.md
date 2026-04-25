# Solar Expanse Multiplayer Mod

This folder contains the first implementation scaffold for a **host/client multiplayer mod** for Solar Expanse.

## Current design goals

- One player hosts a session.
- Other players join by **direct connect** using the host's public IP and port.
- The **host is authoritative for simulation time**.
- Each player is assigned a **company slot** and keeps their own:
  - money
  - resources
  - research
  - missions
  - construction state
- Anything a player builds belongs only to that player's company and is **not merged into other players' companies**.
- The world clock and shared simulation state are intended to be synchronized.

## What is implemented in this scaffold

- BepInEx/Harmony plugin structure
- direct-connect TCP host/client session layer
- JSON message protocol
- company-slot ownership model scaffold
- host-authoritative **time sync** pipeline wired to `Manager.TimeController`
- first-pass **company-state snapshot exchange** for money/research overview data
- first-pass **authoritative application** of remote company money and completed research state
- first-pass **per-company inventory synchronization** for owned object resource lists and construction equipment counts
- first-pass **per-company facility and construction queue synchronization**
- optional **storyline suppression** for contracts, tutorials, and cyclical mission prompts
- simple IMGUI debug/control window for host/join/disconnect

## Ownership model

- Company state is synchronized **per company slot**.
- Inventories are applied only to the owning company's `ObjectInfoData` entries.
- Facilities and construction snapshots are also applied only to the owning company's `ObjectInfoData` entries.
- Host normalization prevents clients from claiming a different company slot than the one assigned by the host.
- This means a player's built assets are treated as that player's company assets, not as shared/global company assets.

## What is NOT finished yet

This is **not yet a fully playable multiplayer mod**. It is the initial foundation. The following systems still need real gameplay replication work:

- authoritative application of full company resource synchronization beyond current owned inventory snapshots
- deeper facility/construction replication beyond current snapshot-based state mirroring
- mission replication
- shared object/world delta replication
- join-in-progress world snapshotting
- desync detection and recovery
- permissions/validation for remote actions
- authoritative active-research queue/progress reconstruction

## Sandbox / storyline suppression

The multiplayer config now supports disabling default storyline-style systems for sandbox play:

- contracts
- tutorials
- cyclical mission prompts

Config key:

- `Gameplay.DisableStorylineSystems = true`

## Expected environment

- Game: Solar Expanse Unity Mono build (`2022.3.22f1` observed)
- Intended loader: **BepInEx 6 Unity Mono**
- Intended patch library: **Harmony / HarmonyX**

## Build outline

1. Install BepInEx into a copy of the game.
2. Restore/build this project.
3. Copy the built DLL into `BepInEx/plugins/`.
4. Launch the game and use the in-game multiplayer debug window.

### Notes about package restore

The project file includes an additional NuGet source for BepInEx packages:

- `https://nuget.bepinex.dev/v3/index.json`

This is required because the default offline feed on this machine does not include the BepInEx Unity Mono support package.

The current scaffold uses:

- `BepInEx.Core` `6.0.0-be.755`
- `BepInEx.Unity.Mono` `6.0.0-be.755`
- `HarmonyX` `2.10.2`

## Source layout

- `src/SolarExpanse.Multiplayer/` — plugin source

## First recommended next milestones

1. Verify plugin loading inside BepInEx.
2. Validate time synchronization between two machines.
3. Expand company-state exchange from current money/completed-research/inventory application into full authoritative state application.
4. Add mission/build/research replication.
