# Daves the Divers: local co-op for Dave the Diver

A BepInEx mod that adds a **second, purple Dave** to *Dave the Diver* for same-screen co-op on one PC. Player 1 plays as usual. Player 2 plays on the right side of the same keyboard or on an Xbox controller.

> **Status: work in progress, pre-release.** Diving together works and the sushi bar is partly co-op. Expect rough edges: see [What works](#what-works) and [docs/DEV_NOTES.md](docs/DEV_NOTES.md). Back up your save before trying it.

This is a separate, new mod. It is not part of the networked [Coop-Divers](https://github.com/S1ii/Coop-Divers) project, though that project is what got this one started.

## What works

**Dives**
- Player 2 dives in with player 1 and swims, dashes, harpoons, uses the knife, guns and helper items.
- Player 2 has their own oxygen tank and HUD (purple), weapon/item panel (bottom-left) and starting loadout. The cargo weight is shared. If either diver runs out of oxygen, the dive ends.
- The camera follows the midpoint between the two divers and keeps player 2 in view.
- Sharks can grab player 2. Player 2 mashes A / B (Enter / Backspace) to break free, and player 1 can harpoon the shark to help. The sawtooth shark's grab is turned off in co-op because it broke the game even for player 1.
- On the boat, player 2 is shown standing next to Dave.

**Sushi bar (night)**
- Player 2 walks the restaurant as a second Dave: serve, clean (alternate LT / RT), throw away, talk, and play the drink/wasabi mini-games.
- While one player is in a mini-game, the other keeps working. *(Player 1's side of this is new and still being tested.)*

**Not yet**
- Anything outside dives and the sushi bar (farm, other side activities, boss fights) is untested.
- Only one mini-game / conversation screen can be open at a time.

## Install

1. Install **BepInEx 6 (Unity IL2CPP, Windows x64)**, version `6.0.0-be.788` (from [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be)). Unzip it into the game folder, then start the game once and close it, so BepInEx generates its `BepInEx\interop` files. The first start takes a while.
2. Put `DaveTheDiverLocalCoop.dll` in `Dave the Diver\BepInEx\plugins\DaveTheDiverLocalCoop\`. You can build it yourself (below), or take it from a release if one is posted.
3. Start the game and load a save. Press **F9**, or **LB + RB** on a controller, to add or remove player 2.

Settings are in `BepInEx\config\dev.davethedivercoop.local.cfg`, which is created on the first run. That includes player 2's keys, the controller choice and the grab block list.

## Controls (player 2, defaults)

| Action | Keyboard | Xbox controller |
|---|---|---|
| Swim | Arrow keys | Left stick |
| Interact / pick up | Right Shift | X |
| OK / hold to aim | Enter | A |
| Back / knife | Backspace | B |
| Fire harpoon | Slash or Right Alt | RT |
| Long dash / short dash | Period / Comma | LT / left stick click |
| Use item / switch item / switch weapon | U / I / O | Y / LB / RB |

With only one controller plugged in, it belongs to player 2 and player 1 plays on keyboard and mouse. You can change this with `[Player2] Player1UsesKeyboard`.

Other keys:
- In the pause menu, **RB / E** switches the Equipment box between the two divers.
- To drop player 2 and reload the last save, hold **Delete** (or **View**) for 2 seconds in the pause menu. Use this if co-op gets stuck.

## Build

You need the .NET 6 SDK, plus BepInEx installed in the game as above, because the build references the game's generated `BepInEx\interop` DLLs.

```
dotnet build -c Release -p:GameInstallPath="<your Steam library>\steamapps\common\Dave the Diver"
```

`GameInstallPath` defaults to the standard Steam folder. The build copies the DLL into the game's `BepInEx\plugins` folder; turn that off with `-p:DeployPlugin=false`. `watch-build.bat` rebuilds automatically whenever a source file changes, waiting until the game is closed before installing.

## How it works (for modders)

The game is Unity IL2CPP, and nearly all of its code assumes there is exactly one player. The mod's main technique is **"player 2's turn"**:

- **The clone.** Player 2 is a runtime copy of player 1's `PlayerCharacter`. `CloneRepair.cs` refills the references Unity doesn't copy. Separate files then give the clone its own copies of state the game shares (input callbacks, effect handlers, ability callbacks, oxygen).
- **Swapping "the player".** When the game would run code for player 2, the mod briefly points the game's single "the player" field (`InGameManager.playerCharacter`) at player 2, runs the game's own method, then switches back (`SecondDiver.BeginPlayer2Turn`). Bites, grabs, the mash bar and helper items all reuse the game's code this way.
- **Input.** Player 2's keys and controller are read straight from Windows (`GetAsyncKeyState` / XInput), so they never reach the game's input meant for player 1. Then:
  - `P2Dispatch` feeds those keys into the game's own `OnDirectHandler`.
  - `KeyboardFilter` gives the game a virtual keyboard that carries only player 1's keys.
  - `P2VirtualPad` gives the game a virtual gamepad when a keyboard-only player 2 needs to play a mini-game.
- **When the game's sequence can't work for player 2,** the mod runs its own version and calls the game's pieces from it. Examples are the shark grab struggle (`P2Grab.cs`) and sushi-bar cleaning.
- **Testing.** `CoopTest.cs` is an in-game test bot that spawns sharks, records what happens and writes `BepInEx\coop_tests.log`. It also runs a mutation pass with the fixes turned off, which must fail; that proves the tests actually catch the bugs. Turn it on with `[Test] AutoRun = true`.

[docs/DEV_NOTES.md](docs/DEV_NOTES.md) has the current bug list and findings, and explains how the game was decompiled with Cpp2IL to read method bodies. `tools/annotate_isil.py` turns Cpp2IL's ISIL dump into something readable.

Most files start with a short comment explaining which problem they solve, so reading `src/` file by file is a good way to learn the tricks.

## License

MIT; see [LICENSE](LICENSE). *Dave the Diver* belongs to MINTROCKET / NEXON. This repo contains no game code or assets: decompiled output stays on your own machine.
