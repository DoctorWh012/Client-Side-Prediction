# Client-Side-Prediction
A step by step guide on client side prediction and server reconciliation in Unity, using real networking code ([Riptide](https://riptide.tomweiland.net/)) instead of pseudo code.

> I wrote the first version of this guide years ago. It had logic errors (the replay part was literally commented out because it "broke" and I didn't know why) and it doesn't represent how I write netcode today. So I rewrote the whole thing. If you read the old one, check [What was wrong with the old version](#what-was-wrong-with-the-old-version) at the end.

# What is client side prediction?
In a server authoritative game the server decides where you are. If the client had to wait for the server before moving, every key press would take a full round trip to show up on screen, at 150ms ping that feels horrible.

So the client **predicts**: it runs the exact same movement code the server will run, on the exact same inputs, the moment you press the key. When the server's answer arrives it should match what we already predicted, and you never notice the ping.

# Server reconciliation
Sometimes the prediction is wrong. Inputs got lost, the server did something the client didn't know about (knockback, a teleport, another player in the way...). The server is the authority so it always wins.

The client keeps a history of every input it sent and every state it predicted. When the server says "on tick 500 you were here", the client compares that to what it predicted for tick 500. If they don't match, it snaps back to the server's state for tick 500 and **replays every input after it**, ending up at a corrected present.

# Requirements
- Unity 2022.3 (the project is on 2022.3.62f3)
- [Riptide Networking](https://riptide.tomweiland.net/) 2.0.0, already in the package manifest. The concepts work with any networking library
- Deterministic movement. This uses a `CharacterController`, not a `Rigidbody`. Both sides have to get the same result from the same input, so Unity's physics simulation is out

# Running the demo
Open `Assets/Scenes/SampleScene.unity` and press play. The server and the client run in the same process so you can see both:
- **Orange** is your predicted player. It moves the instant you press WASD / Space
- **Green** is the server's copy. It trails behind by your ping

On the `NetworkManager` you can change **Simulated Ping**, **Simulated Jitter** and **Simulated Packet Loss** while playing, and on the player prefabs **Wanna Move Dumb** makes the player walk around randomly on its own.

The HUD shows the unacknowledged inputs (roughly your ping in ticks) and how many times the client had to reconcile.
> With 200ms ping, ±20ms jitter and even 30% packet loss that counter stays at **0**. The client predicts every single tick right. You only see it go up when something actually disagrees, try moving the green capsule in the scene view while playing

# How to
Let's go step by step. Everything here is in [`PlayerMovement.cs`](Assets/Scripts/Player/PlayerMovement.cs) unless I say otherwise.

## 1) Ticks
Prediction only works if the client and the server simulate in the same steps. If the client simulates in `Update` with a variable `deltaTime` and the server doesn't, they will never agree.

So everything runs at a fixed tick rate. Unity already has a fixed timestep, `FixedUpdate`, so we just set it to our tick rate in the [`NetworkManager`](Assets/Scripts/Multiplayer/NetworkManager.cs)
```cs
Time.fixedDeltaTime = 1f / tickRate;
```
And instead of every player having its own `FixedUpdate`, the `NetworkManager` drives the tick so the order is always the same: receive messages -> simulate -> send messages
```cs
private void FixedUpdate()
{
    if (Server.IsRunning)
    {
        Server.Update();
        foreach (Player player in Player.ServerList.Values) player.playerMovement.ServerTick();
    }

    Client.Update();
    foreach (Player player in Player.List.Values) if (player.IsLocal) player.playerMovement.ClientTick();

    FlushDelayedMessages();
}
```
> `FlushDelayedMessages` is the fake ping/packet loss, more on it [later](#7-faking-a-bad-connection)

## 2) Inputs and states
Two things get saved every tick, what the player pressed and where that put them.

The input is just a bitmask, there's no reason to send floats for keys that are either pressed or not. One byte per tick.
```cs
[Flags]
public enum InputButtons : byte
{
    None = 0,
    Forward = 1 << 0,
    Backward = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    Jump = 1 << 4,
}

public struct PlayerInput
{
    public InputButtons buttons;
    public uint tick;
}
```
The state is **everything the movement code reads that isn't the input**. This is the part people get wrong (I did). If the movement reads something that isn't saved here, a replay starts from the wrong state and gives a different result.
```cs
public struct SimulationState : IMessageSerializable
{
    public Vector3 position;
    public Vector3 velocity;
    public bool grounded;
    public uint tick;
}
```
> Velocity is in there because gravity and jumping carry over from tick to tick. Grounded too, because whether you can jump depends on the last tick's collision

Both get stored in ring buffers indexed by tick. Tick 1025 goes in slot 1, overwriting tick 1, which is fine because we never need anything that old.
```cs
public const uint BufferSize = 1024;

private readonly PlayerInput[] inputBuffer = new PlayerInput[BufferSize];
private readonly SimulationState[] stateBuffer = new SimulationState[BufferSize];
```
> Ticks are `uint`. A `ushort` tick wraps around after 65535 ticks, that's 17 minutes at 64hz, and after that every "is this newer" check breaks

## 3) The simulation
This is the one function both the client and the server run. Same input + same starting state = same result, that's the whole trick.
```cs
private void Simulate(PlayerInput input)
{
    float delta = Time.fixedDeltaTime;
    Vector3 moveDir = GetMoveDir(input);

    velocity.x = moveDir.x * movementSettings.moveSpeed;
    velocity.z = moveDir.z * movementSettings.moveSpeed;

    if (grounded && velocity.y < 0) velocity.y = 0;
    if (grounded && input.IsPressed(InputButtons.Jump)) velocity.y = Mathf.Sqrt(2f * movementSettings.jumpHeight * movementSettings.gravity);
    velocity.y -= movementSettings.gravity * delta;

    CollisionFlags collisionFlags = controller.Move(velocity * delta);

    grounded = (collisionFlags & CollisionFlags.Below) != 0;
    if ((collisionFlags & CollisionFlags.Above) != 0 && velocity.y > 0) velocity.y = 0;
}
```
> Notice it uses `grounded` from the last `Move` and not `controller.isGrounded`. The controller's flag doesn't get rewound with our state, so during a replay it would be the grounded of the present, not of the tick being replayed

The movement values come from a `PlayerMovementSettings` ScriptableObject, so you can tweak them without touching code.

## 4) The client tick
Every tick the local player does this:
```cs
public void ClientTick()
{
    // Fix the past before predicting the new tick
    Reconcile();

    currentTick++;
    uint cacheIndex = currentTick % BufferSize;

    PlayerInput input = new PlayerInput { buttons = GetInput(), tick = currentTick };
    inputBuffer[cacheIndex] = input;

    previousTickPosition = transform.position;
    Simulate(input);

    // Saved AFTER simulating, it's what the server sends back for this tick
    stateBuffer[cacheIndex] = CurrentSimulationState(currentTick);

    SendClientInputs();
}
```
Reconcile, read input, save it, simulate it right away (that's the prediction), save the result, send the input.

The state is saved **after** simulating on purpose. When the server processes input 500 it sends back where input 500 left you, so that's what we need to compare against.

#### Sending the inputs
The inputs are sent as `Unreliable`, packets can get lost. So instead of sending only the newest input, the client sends **every input the server hasn't acknowledged yet**, every tick.

Why? Simple, if a packet gets lost the next one carries the same inputs again, and the server never misses one. Since ticks are consecutive we only need to send the first tick and then one byte per input.
```cs
private void SendClientInputs()
{
    uint firstTick = currentTick - lastAckedTick > MaxRedundantInputs ? currentTick - MaxRedundantInputs + 1 : lastAckedTick + 1;

    byte[] inputs = new byte[currentTick - firstTick + 1];
    for (int i = 0; i < inputs.Length; i++) inputs[i] = (byte)inputBuffer[(firstTick + (uint)i) % BufferSize].buttons;

    Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.playerInputs);
    message.AddUInt(firstTick);
    message.AddBytes(inputs);
    NetworkManager.Singleton.SendToServer(message);
}
```
> It's capped at `MaxRedundantInputs` (64, about a second) so a long lag spike can't make the message grow forever

## 5) The server
#### Receiving the inputs
Most of what arrives is stuff we already have, so the server only keeps inputs newer than the last one it received and queues them.
```cs
private void HandleClientInputs(uint firstTick, byte[] inputs)
{
    for (int i = 0; i < inputs.Length; i++)
    {
        uint tick = firstTick + (uint)i;

        // Redundant copy of an input we already have
        if (tick <= lastReceivedInputTick) continue;

        // Lost more than the redundancy covers, the client will reconcile
        if (tick > lastReceivedInputTick + 1) Debug.LogWarning($"[prediction] client {player.Id} inputs {lastReceivedInputTick + 1}-{tick - 1} never arrived, skipping them");

        inputQueue.Enqueue(new PlayerInput { buttons = (InputButtons)inputs[i], tick = tick });
        lastReceivedInputTick = tick;
    }

    if (inputQueue.Count <= MaxQueuedInputs) return;

    uint firstDroppedTick = inputQueue.Peek().tick;
    while (inputQueue.Count > MaxQueuedInputs) inputQueue.Dequeue();
    Debug.LogWarning($"[prediction] client {player.Id} input queue backed up, dropped inputs {firstDroppedTick}-{inputQueue.Peek().tick - 1}");
}
```
> The cap at the end matters more than it looks. Inputs arrive at 1 per tick and get processed at 1 per tick, so if a pile of them ever builds up (the client comes back from a lag spike with a second of inputs) it would **never** drain, and that second would be added as input delay forever. Past `MaxQueuedInputs` the oldest ones get thrown away, the client reconciles once and everything is back to real time

#### Processing them
The inputs are **not** processed the moment they arrive. They're processed in the server's tick, one per tick, same as the client made them.

If the server just ran everything it received, a modified client could send 2 inputs per tick and move at double speed. So each player gets a budget of one input per tick. It can bank a few (`MaxInputBudget`) so it can catch up after jitter or a lost packet, but on average nobody moves faster than real time. Same idea as Source engine's `sv_maxusrcmdprocessticks`.
```cs
public void ServerTick()
{
    previousTickPosition = transform.position;

    inputBudget = Mathf.Min(inputBudget + 1, MaxInputBudget);
    if (inputQueue.Count == 0) return;

    while (inputBudget > 0 && inputQueue.Count > 0)
    {
        PlayerInput input = inputQueue.Dequeue();
        Simulate(input);
        lastProcessedInputTick = input.tick;
        inputBudget--;
    }

    SendServerState(CurrentSimulationState(lastProcessedInputTick));
}
```
After simulating, the server sends back the resulting state, stamped with the tick of the **last input it processed**. That tick is how the client knows which of its predictions to compare.

## 6) Reconciliation
When a state arrives from the server the client just stores it, ignoring anything older than what it already has (unreliable messages can arrive late, twice, or out of order).
```cs
private void HandleServerState(SimulationState serverState)
{
    if (serverState.tick <= lastAckedTick) return;

    latestServerState = serverState;
    lastAckedTick = serverState.tick;
    hasNewServerState = true;
}
```
Then at the start of the next tick:
```cs
private void Reconcile()
{
    if (!hasNewServerState) return;
    hasNewServerState = false;

    uint serverTick = latestServerState.tick;
    uint cacheIndex = serverTick % BufferSize;
    SimulationState predictedState = stateBuffer[cacheIndex];

    float positionError = Vector3.Distance(predictedState.position, latestServerState.position);
    float velocityError = Vector3.Distance(predictedState.velocity, latestServerState.velocity);
    if (positionError <= movementSettings.reconciliationThreshold && velocityError <= movementSettings.reconciliationThreshold && predictedState.grounded == latestServerState.grounded) return;

    // Rewind to the server's state then replay every input since
    ApplySimulationState(latestServerState);
    stateBuffer[cacheIndex] = latestServerState;

    for (uint tick = serverTick + 1; tick <= currentTick; tick++)
    {
        uint replayIndex = tick % BufferSize;
        Simulate(inputBuffer[replayIndex]);
        stateBuffer[replayIndex] = CurrentSimulationState(tick);
    }
}
```
> Trimmed, the real one also handles a server state older than our whole history and the visual smoothing below

Compare what we predicted for that tick with what the server says. If they match, we were right, nothing to do. If not, apply the server's state and replay every input from the tick after it up to now, overwriting the history as we go so the next comparison is against the corrected states.

There's one Unity specific gotcha here:
```cs
private void ApplySimulationState(SimulationState state)
{
    transform.position = state.position;
    velocity = state.velocity;
    grounded = state.grounded;

    Physics.SyncTransforms();
}
```
`CharacterController` keeps its own copy of the position. If you set `transform.position` and call `Move()` right after without `Physics.SyncTransforms()`, the move starts from where the controller **thinks** it is, so the whole replay is wrong. This was one of the reasons the old version of this guide "broke" when replaying.

#### Smoothing the corrections
The simulation snaps instantly on a correction, but the mesh doesn't have to. The player's mesh is a child called `Visual`. It gets interpolated between the last two ticks so it's smooth at any framerate, and when a correction happens, the jump is stored as an offset that eases out over a few frames.
```cs
private void Update()
{
    visualCorrectionOffset = Vector3.Lerp(visualCorrectionOffset, Vector3.zero, 1f - Mathf.Exp(-movementSettings.correctionSmoothing * Time.deltaTime));

    float tickFraction = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
    visual.position = Vector3.Lerp(previousTickPosition, transform.position, tickFraction) + visualCorrectionOffset;
}
```
> Corrections bigger than `maxSmoothedCorrection` aren't smoothed, a teleport should look like a teleport

## 7) Faking a bad connection
Prediction on localhost proves nothing, there's no ping. So everything gameplay sends goes through `SendToServer` / `SendToClient` on the `NetworkManager`, which holds each message for half the simulated ping (± jitter) and randomly drops unreliable ones.
```cs
private void DelayMessage(Message message, bool toServer, ushort toClientId)
{
    if (message.SendMode == MessageSendMode.Unreliable && UnityEngine.Random.value * 100f < simulatedPacketLoss)
    {
        message.Release();
        return;
    }

    float oneWayDelay = (simulatedPing * 0.5f + UnityEngine.Random.Range(-simulatedJitter, simulatedJitter)) / 1000f;
    delayedMessages.Add(new DelayedMessage { ... });
}
```
> In a real game you'd just call `Client.Send` / `Server.Send` directly. Reliable messages are never dropped here, Riptide would just resend them

# What was wrong with the old version
If you used the old version of this guide, these are the bugs it had:
- **The replay was commented out.** Reconciliation snapped the position but never replayed the inputs. The reasons it "broke":
  - Velocity wasn't part of the saved state, so gravity and jumps replayed from the wrong velocity
  - `CharacterController` wasn't synced after the teleport (see `Physics.SyncTransforms()` above)
  - The state was saved **before** applying the input, while the server sent the state **after** it. Every comparison was off by one tick
- **The server skipped the newest input** in every message (`i < inputs.Length - 1`), so it was always a tick behind
- **The current tick's input wasn't sent**, it only went out on the next tick
- **The redundant input count was a `byte` with no cap.** If the server didn't answer for 256+ ticks it overflowed and the message was garbage
- **`ushort` ticks**, every "is this newer" comparison broke once the tick wrapped around (~17 minutes at 64hz)
- **Movement ignored delta time**, speed and gravity were applied per tick, so changing the tick rate changed how fast you moved
- **The fake ping was fake.** It used `Invoke` to delay *calling* `SendInput`, which then read whatever the inputs were at that moment, not when they were made
- **The server trusted the client's input rate**, so a modified client could speedhack by sending more inputs

# Going further
This covers the local player. In a real game you also need:
- **Other players.** You don't predict them, you interpolate them between the states the server sends, slightly in the past. That's snapshot interpolation
- **Lag compensation.** Since everyone sees other players in the past, the server has to rewind them when checking if a shot hit
- **Time dilation.** The server here never invents inputs, if the queue runs dry the player just waits for the next one. Games like Overwatch instead make the client run slightly faster or slower to keep the server's input queue at just the right size

# Credits
These helped me a lot, both back then and now:
- [Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/) - Glenn Fiedler
- [Fast-Paced Multiplayer](https://www.gabrielgambetta.com/client-server-game-architecture.html) - Gabriel Gambetta
- [Source Multiplayer Networking](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking) - Valve
- [Overwatch Gameplay Architecture and Netcode](https://www.youtube.com/watch?v=W3aieHjyNvw) - Timothy Ford, GDC 2017
- [Client-Side Prediction With Physics In Unity](https://www.codersblock.org/blog/client-side-prediction-in-unity-2018)
- [Seamless fast paced multiplayer in Unity3D](https://medium.com/@christian.tucker_68732/seamless-fast-paced-multiplayer-in-unity3d-implementing-client-side-prediction-ab520bf49bd1)

If anything is wrong or missing feel free to contact me.
