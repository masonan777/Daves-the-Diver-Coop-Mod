# Developer notes

Working notes: what has been found, what is fixed and what still needs testing. The README has the overview; this file is for anyone picking the code up.

- **Mod source:** `src/` (BepInEx 6 IL2CPP + HarmonyX).
- **Settings:** `BepInEx\config\dev.davethedivercoop.local.cfg`.
- **Logs:** `BepInEx\LogOutput.log` (everything) and `BepInEx\coop_tests.log` (test bot and diagnostics).

## Test bot (CoopTest.cs)
- `[Test] AutoRun = true` runs this sequence: title → Continue → holds the boat's dive button → joins P2 → grab suite → a second pass with the mod's grab rules off (it must FAIL) → quits.
  - It uses the normal dive setup rather than jumping straight to a scene with `GoToInGameEntry`, because that leaves most sharks out.
- **Suite:** `Harpoon,Saw,Whitetip,Tiger_Shark`, set by `[Test] GrabSuite`. The on-screen banner says which pass is running.
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
- **P2 flashing / bleeding when P1 is attacked:** still open, maybe not a bug.
  - The bite path (`PlayerCharacter.OnBiteHit`) never fired in a test run.
  - P2 has its own `CharacterEffectHandler`.
  - The flash is `PlayerCharacter.BlinkPlayer`. `BleedWatch` (in `BleedTrace.cs`) logs every flash with its source and the creatures near each diver.
  - Latest log: every P2 flash came from a plain hit (not a bite or `DoDamageEvent`), with no big creature near either diver. One lined up exactly with a parrotfish ram (camera locked onto it and shook), so at least some are real hits. `Nearby()` only lists sharks, eels and similar, not parrotfish or small fish.
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

## Harpoon
- **Aim:** the game allows one aim at a time. If P2 holds aim and isn't aiming 3 frames later, the mod calls P2's `OnStartRangeAttack` (`SecondDiver.cs`). `P1AimFix` (`P2Interact.cs`) does the same for P1 while P2 aims. Bot test `Harpoon` passes in both orders.
- **Reel-in gauge:** the mash / stick-shake gauge (`StickShakeFightUI.LateUpdate`) is drawn over P2 when P2 is the one fighting (`HarpoonFightUI.cs`). Not yet confirmed in play. The direction-sequence gauge (`CommandFightUI`) isn't handled.

## Camera spots / photo zones (P2Interact.cs; untested)
- A camera spot is an `InteractionGimmick_PhotoZone` (an `IInteractionObject`). A trigger's UnityEvent calls `InteractionGimmick.OnPlayerEnter(true)`, which always calls `EnqueueInteractObject` on "the player", so only P1 ever got it. Pressing interact runs `SuccessInteract` -> `OnSuccess` -> `PhotoZone.StartPhotoZoneMiniGame`.
- An earlier reach scan around P2 only found fish bodies: the gimmick component isn't on its collider's own object.
- Now: every 0.25 s the mod checks each `InteractionGimmick`'s trigger colliders against P2 and adds it to / removes it from P2's interaction queue (no turn swap). P2's interact press already runs as P2's turn, so the photo zone starts from P2.
- `PhotoZoneStartPatch`: when a photo zone starts during P2's turn, `PhotoP2` lends P2's controller (or the keyboard pad) to the game, and keeps P1's keys out, until `PhotoZone.Current` closes.
- Logs: `P2 interact: <name> added for player 2`, `Photo zone <name> starting (player 2's turn=...)`, `Player 2's photo zone over`.

## Other open issues
- **P2's keys briefly controlled both divers** once at dive start, then it went back to normal. Cause unknown; the old `P2Interact`, which swapped turns 4x a second, was the main suspect and no longer does.
- **Green Humphead Parrotfish "QTE":** never appeared for either diver. Fish databases list it as defensive with no QTE, and it seems to just ram, so this is probably not a bug.
- **Farm:** needs co-op work like the sushi bar. Untested.
