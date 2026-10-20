# Developer notes

Working notes: what has been found, what is fixed and what still needs testing. The README has the overview; this file is for anyone picking the code up.

- **Mod source:** `src/` (BepInEx 6 IL2CPP + HarmonyX).
- **Settings:** `BepInEx\config\dev.davethedivercoop.local.cfg`.
- **Logs:** `BepInEx\LogOutput.log` (everything) and `BepInEx\coop_tests.log` (test bot and diagnostics).

## Test bot (CoopTest.cs)
- `[Test] AutoRun = true` runs this sequence: title → Continue → holds the boat's dive button → joins P2 → grab suite → a second pass with the mod's grab rules off (it must FAIL) → quits.
  - It uses the normal dive setup rather than jumping straight to a scene with `GoToInGameEntry`, because that leaves most sharks out.
- **Suite:** `Saw,Whitetip,Tiger_Shark`, set by `[Test] GrabSuite`. The on-screen banner says which pass is running.
- **Manual test keys:**
  - Left Shift + F3 runs the suite; Left Shift + F4 runs it with the rules off.
  - F3 / F4 / F5 spawn a test fish next to player 1.
  - F6 refills both divers' oxygen and unlocks them.
- Set `AutoRun = false` to play normally. The bot does not cover the sushi bar.
- Patch classes with `Trace` in their name only load with `[Debug] EnableTraceHooks = true`. Some damage-handling hooks crashed the game, so they stay off by default; name always-on diagnostics something else.

## Decompiling the game (to read method bodies)
Il2CppInterop gives you signatures only. To see what a game method actually does:
1. Run [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) (`2022.1.0-pre-release.21`) against `GameAssembly.dll` + `DaveTheDiver_Data\il2cpp_data\Metadata\global-metadata.dat`:
   - `--output-as diffable-cs`: every class as C# signatures with field offsets (~16 s).
   - `--output-as isil`: every method body as Cpp2IL's intermediate language (~1 min, ~800 MB).
2. The ISIL calls raw addresses. `src/RvaDump.cs` (in game), or any IL2CPP method-address dump, gives `RVA name` lines. `tools/annotate_isil.py <rva-map> <outdir> <isil files…>` keeps only the ISIL and replaces call addresses with method names.
3. Handy offsets:
   - `[Singleton.Instance + 168]` in game code is `InGameManager.playerCharacter`, i.e. "the player". The mod's "player 2's turn" swaps exactly this field.
   - `PlayerCharacter + 0x5D1` is `IsBattleQTEState`.

Keep decompiled output out of the repo. It is the game's code.

## P2 grabs (P2Grab.cs, P2MashUI.cs, NoSharkGrab.cs, GrabRedirect.cs, BiteRedirect.cs)
- The game's grab/QTE sequences only work on "the player" (P1). Grabs aimed at P2 are skipped, and the mod holds P2 itself: a copy of the game's mash bar, mash A/B, up to 8 s.
- `[Coop] GrabBlockList` (default `Saw`) turns a creature's grab off for both divers.
- The game's mash prompt (`AniClipEvents.OpenQTEUI.Open`) and `CommonQTEStart` on P2 are blocked. P2 gets `CommonQTEStart` from the mod for the struggle pose.

### Findings from the decompile
- **Biting fish (whitetip) stuck in its charge animation after P2 broke free.**
  - On the game's own mash result (`Biteable.OpenQTE` callback), the game sends the fish `SetTrigger(UserQTESucces0)` on success or `SetTrigger(UserQTEFail0)` on fail, then calls `BiteSkillEnd`.
  - The mod only called `BiteSkillEnd`. `P2Grab.PlayResult` now sends the trigger too.
  - It also runs `BiteSkillEnd` during P2's turn. `BiteSkillEnd` clears "the player's" `IsBattleQTEState` and calls `ReleaseStuckDamageAnim`, which used to hit P1.
- **P1 panicking when P2 is grabbed.**
  - A creature animation event, `AniClipEvents.PlayDaveAnimation.Fire`, calls `StartBattleQTE`, sets the `PlayDaveAnim` trigger and sets the QTE animation key on "the player".
  - `P2PanicAnimation` runs it during P2's turn when the creature really holds, is attached to, or bite-targets P2.
  - Block-list creatures' grab animation is skipped for both divers.
  - The whitetip fires this event again right after P2 breaks free, as part of its success animation.
- **A shark holding P2 also grabbed P1,** leaving both divers in its mouth. `NoSharkGrab.Skip` now skips grabs on P1 by the creature that holds P2.
- **P2 flashing / bleeding when P1 is attacked:** still open.
  - The bite path (`PlayerCharacter.OnBiteHit`) never fired in a test run.
  - P2 has its own `CharacterEffectHandler`.
  - The flash is `PlayerCharacter.BlinkPlayer`. `BleedWatch` (in `BleedTrace.cs`) logs every flash with its source and the creatures near each diver.
- **Mako:** it doesn't grab, so it's not part of the suite.

## Sushi bar
- **Basic setup (SushiCoop.cs):** P2 is a copy of the restaurant `StaffDave`. The mod calls `OnInteraction_Impl` on P2's copy with the game's "Dave" swapped to P2 (`SushiBarStaffManagerImpl.m_Dave`).
- **Cleaning:** the game's clean screen takes everyone's controls, so P2's cleaning is counted by the mod (alternate LT / RT).
- **P1 free while P2 is in a mini-game (SushiP1Drive.cs, P2VirtualPad.cs; still being tested):**
  - **Why P1 was locked:** a mini-game takes over the game's input, and P1's restaurant Dave only gets controls through `StaffDave.OnDirectHandler`.
  - **The fix:** while P2's screen is open, the mod drives P1 the same way it drives P2. P1 moves through his own `SushiBarPlayerHanlder`, and Serve / Trash / CleanLeft / CleanRight go through `OnInteraction_Impl`.
  - **P1's keys** are read from P1's own bindings in the game's "Dave" input map.
  - **Keeping P1's keys out of P2's mini-game:** the keyboard filter is forced on and P1's keys are muted.
  - **P2 on a keyboard:** P2 plays the mini-game on a virtual gamepad fed from P2's keys. Arrows = stick, Enter = A, Backspace = B, Right Shift = X, U = Y, Period = LT, Slash = RT.
  - **Not offered to P1 meanwhile:** mini-games, talking, special (one screen at a time), dash, pause.
- **`StaffDave.OnDirectHandler` table** (Dave map action → `SushiBarInteraction`):

  | Dave map action | Interaction |
  |---|---|
  | `Interaction` | Talk |
  | `Sushi_Serve` | Serve |
  | `Sushi_Trash` | Trash |
  | `Sushi_Special` | Special |
  | `Sushi_QTE` | QTE |
  | `Sushi_CleanLeft` / `Sushi_CleanRight` | CleanLeft / CleanRight |

  `Sushi_Dash` sets dash.
