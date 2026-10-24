# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

A Unity **2022.3.62f3** demo + step-by-step guide ([README.md](README.md)) for client side prediction and server reconciliation over **Riptide 2.0.0** (git package in `Packages/manifest.json`). The README is the product: it quotes code from `Assets/Scripts/` (verbatim, sometimes with comments trimmed), so **any code change must be mirrored in the matching README snippet** (and vice versa).

## Building, running, verifying

- No CLI build and no test suite in the repo. Run it by opening `Assets/Scenes/SampleScene.unity` and pressing Play.
- All scripts compile into the default `Assembly-CSharp` (no asmdefs).
- The user usually has the project open in the Editor, which locks it, so batchmode can't run on the repo itself. To compile-check or play-test headlessly, copy `Assets/`, `Packages/`, `ProjectSettings/` and `Library/` into the scratchpad and run the Editor there:
  ```
  "C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe" -batchmode -nographics -projectPath <copy> -runTests -testPlatform PlayMode -testResults <copy>/../results.xml -logFile <copy>/../test.log
  ```
  PlayMode tests need the scripts in an asmdef (`CSP`, referencing `RiptideNetworking` and `Unity.TextMeshPro`) plus a test asmdef. Add both **only in the copy**. For scene or prefab changes, use an `-executeMethod` editor script in the copy, then copy the resulting `.unity`/`.prefab`/`.asset` files and their `.meta` files back. Don't hand-edit scene YAML.
- When adding scripts or folders while the Editor is open, write their `.meta` files yourself, with fresh GUIDs, before Unity generates its own. That keeps GUIDs stable between the repo and any scratch copy.

## Architecture

**Loopback host.** `NetworkManager` starts a Riptide `Server` *and* `Client` in the same process (`Server.Start(port, 1)` then connect to `127.0.0.1`). The server's copy of the player (green, `serverPlayerPrefab`) and the predicted copy (orange, `localPlayerPrefab`) both exist in the scene. They only communicate through messages. Code on one side must never read the other side's objects directly.
- `Player.List` holds the client's players and `Player.ServerList` the server's. Same ids, separate dictionaries, because both sides share one process. `Player.IgnoreCollisionWithOtherPlayers` stops the two overlapping `CharacterController`s from pushing each other.
- Spawn flow: `Server.ClientConnected` → `Player.SpawnServerPlayer` → reliable `playerSpawned` to that client → `SpawnLocalPlayer`.

**Tick.** `Time.fixedDeltaTime = 1 / tickRate` (64). `NetworkManager.FixedUpdate` is the only tick driver, in a fixed order: `Server.Update` → each `ServerList` player's `ServerTick()` → `Client.Update` → the local player's `ClientTick()` → `FlushDelayedMessages`. Players have no `FixedUpdate` of their own.

**Prediction loop** (all in [PlayerMovement.cs](Assets/Scripts/Player/PlayerMovement.cs)):
- `Simulate(PlayerInput)` is the single function both sides run. It must stay deterministic: it may only read the input and fields that are in `SimulationState` (position, velocity, grounded). Anything new that affects movement must be added to `SimulationState` (including its `Serialize`/`Deserialize`) or replays will diverge. Use `grounded` from the last `Move`'s `CollisionFlags`, never `controller.isGrounded`.
- Client: `Reconcile()` → `currentTick++` → store input → `Simulate` → store the state **after** simulating → `SendClientInputs()`. The input and state ring buffers are `BufferSize` (1024), indexed `tick % BufferSize`.
- Inputs are a `byte` bitmask (`InputButtons`). Every tick the client resends all un-acked inputs (capped at `MaxRedundantInputs`) as `firstTick` + `byte[]`.
- Server: `HandleClientInputs` dedupes by `lastReceivedInputTick` and queues the inputs. Past `MaxQueuedInputs` it drops the oldest, so a backlog can't become permanent latency. `ServerTick` processes them under a per-tick budget (`MaxInputBudget`, anti-speedhack), then sends back `SimulationState` stamped with the last **processed input tick**.
- Reconcile compares that state to `stateBuffer[tick]`. On a mismatch it applies the server state and replays inputs `tick+1..currentTick`. `ApplySimulationState` must call `Physics.SyncTransforms()`, otherwise `CharacterController` moves from its stale internal position.
- The visible mesh is the `Visual` child. It is interpolated between ticks in `Update`, and corrections ease out via `visualCorrectionOffset`. The root transform is simulation-only.
- Ticks are `uint` everywhere. Don't use `ushort` ticks (they wrap after ~17 min).

**Network simulation.** Gameplay never calls `Client.Send`/`Server.Send` directly. It uses `NetworkManager.Singleton.SendToServer` / `SendToClient`, which apply `simulatedPing` (half each way), `simulatedJitter` and `simulatedPacketLoss` (unreliable messages only) by holding pooled Riptide messages until they're due. The README relies on this: with no forced desync, `ReconciliationCount` should stay 0 even at 200ms ping, ±20ms jitter and 30% loss.

**Messages.** IDs are the `ServerToClientId` / `ClientToServerId` enums at the top of `NetworkManager.cs`, with implicit values, so append and never reorder. Handlers are `static` methods with `[MessageHandler((ushort)...)]`, living in the class they concern. They read values inline and pass them to an instance `Handle*` method.

## Code style (mirrors the author's game KILLNETIC)

- Singleton property pattern on managers (`NetworkManager.Singleton`); `Client`/`Server` as `{ get; private set; }`.
- `#region` blocks named `ServerSenders`, `ClientSenders`, `ServerToClientHandlers`, `ClientToServerHandlers`, plus feature regions (`Ticks`, `Moving`, `Prediction`, `Authority`).
- Senders `Send*`, receivers `Get*`, logic `Handle*`. Single-line `if`s without braces for one statement. `[Header]` + `[Space(5)]` groups on serialized fields, `[SerializeField] public` for component references.
- Tunables live in ScriptableObjects (`PlayerMovementSettings`, asset in `Assets/Settings/`), not hardcoded. Protocol limits are `const` on the class that uses them.
- Logs are lowercase with a bracket tag: `[prediction] ...`, `[disconnect] ... - reason: ...`, `[spawn] ...`.
- Comments explain *why* (what breaks otherwise), not what the line does.
- The `wannaMoveDumb` debug toggle feeds random inputs. Keep it, it's how the demo and tests drive movement without a keyboard.
- Unlike KILLNETIC, don't leave replaced code commented out above the fix. The README quotes these files, so they stay clean.
