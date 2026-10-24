# Client-Side-Prediction
A step by step guide on how to do client side prediction and server reconciliation in Unity, using actual networking code ([Riptide](https://riptide.tomweiland.net/)) instead of pseudo code.

> This is a rewrite. The first version had logic errors, the replay part was even commented out because it "broke" and I didn't know why. It also doesn't represent how I write netcode today, so I redid the whole thing. If you followed the old one, the bugs it had are listed [at the end](#what-was-wrong-with-the-old-version).

# What is client side prediction?
In a server authoritative game the server decides where you are. If the client had to wait for the server before moving, every key press would take a full round trip to show up on screen, and at 150ms ping that feels horrible.

So the client predicts its own movement. It runs the same movement code the server runs, with the same inputs, as soon as you press the key. When the server's answer arrives it should match what the client already did, so you never notice the ping.

# Server reconciliation
Sometimes the prediction is wrong. Inputs got lost, or the server did something the client didn't know about (knockback, a teleport, another player in the way). As our environment is server authoritative we always trust the server.

The client keeps every input it sent and every state it predicted. When the server says "on tick 500 you were here", the client compares that to what it predicted for tick 500. If they don't match, it goes back to the server's state for tick 500 and processes every input after it again, which gives it the corrected present.

# Requirements
In this demo I am using [Riptide Networking](https://riptide.tomweiland.net/) 2.0.0 (it's already in the package manifest), but you can use any networking library.

The project is on Unity 2022.3.62f3.

This only works with deterministic movement, the client and the server have to get the same result from the same input. This means no Rigidbody movement, the player uses a `CharacterController`.

# Running the demo
Open `Assets/Scenes/SampleScene.unity` and press play. The server and the client run in the same process so you can see both of them:
- Orange is your predicted player, it moves the moment you press WASD / Space
- Green is the server's copy, it trails behind you by your ping

On the `NetworkManager` you can change Simulated Ping, Simulated Jitter and Simulated Packet Loss while playing. On the player prefabs there's Wanna Move Dumb, which makes the player walk around randomly on its own.

The HUD shows how many inputs the server hasn't acknowledged yet (roughly your ping in ticks) and how many times the client had to reconcile.
> With 200ms ping, ±20ms jitter and even 30% packet loss that counter stays at 0, the client predicts every tick right. It only goes up when something actually disagrees, you can try it by moving the green capsule in the scene view while playing

# How to
Now let's go step by step on how it works. Everything here is in [`PlayerMovement.cs`](Assets/Scripts/Player/PlayerMovement.cs) unless I say otherwise.

## 1) Ticks
The client and the server have to simulate in the same steps. If the client moves in `Update` with a variable `deltaTime` and the server doesn't, they will never agree.

So everything runs at a fixed tick rate. Unity already has a fixed timestep, `FixedUpdate`, so in the [`NetworkManager`](Assets/Scripts/Multiplayer/NetworkManager.cs) we just set it to our tick rate
```cs
Time.fixedDeltaTime = 1f / tickRate;
```
Instead of every player having its own `FixedUpdate`, the `NetworkManager` runs the tick for everyone, so the order is always the same: receive messages, simulate, send messages
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
> `FlushDelayedMessages` is the fake ping and packet loss, I'll get to it [later](#7-faking-a-bad-connection)

## 2) Inputs and states
Every tick we save two things, what the player pressed and where that put them.

The input is a bitmask. Keys are either pressed or not, so there's no reason to send floats, it's one byte per tick.
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
The state is everything the movement code reads that isn't the input. This is the part I got wrong in the old version. If the movement reads something that isn't saved here, a replay starts from the wrong state and ends up somewhere else.
```cs
public struct SimulationState : IMessageSerializable
{
    public Vector3 position;
    public Vector3 velocity;
    public bool grounded;
    public uint tick;
}
```
> Velocity is in there because gravity and jumping carry over from one tick to the next. Grounded too, because whether you can jump depends on the last tick's collision

Both get saved in arrays that work as ring buffers, the index is the tick % the buffer size. So tick 1025 goes in slot 1 and overwrites tick 1, which is fine because we never need anything that old.
```cs
public const uint BufferSize = 1024;

private readonly PlayerInput[] inputBuffer = new PlayerInput[BufferSize];
private readonly SimulationState[] stateBuffer = new SimulationState[BufferSize];
```
> Ticks are `uint`. The old version used `ushort`, which wraps around after 65535 ticks, that's about 17 minutes at 64hz, and after that every "is this newer" check breaks

## 3) The simulation
This is the function both the client and the server run. Same input and same starting state give the same result, and that's what makes prediction work.
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
> As you can see it uses `grounded` from the last `Move` and not `controller.isGrounded`. The controller's flag doesn't get rewound with our state, so during a replay it would be the grounded of the present, not of the tick we're replaying

The movement values come from a `PlayerMovementSettings` ScriptableObject so you can tweak them without touching the code.

## 4) The client tick
Every tick the local player does this
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
First it reconciles, then it gets the input and saves it, simulates it right away (this is the prediction), saves the result and sends the input to the server.

The state is saved after simulating on purpose. When the server processes input 500 it sends back where input 500 left you, so that's what we need to compare against.

#### Sending the inputs
The inputs are sent as `Unreliable`, so some packets will get lost. Instead of sending only the newest input, the client sends every input the server hasn't acknowledged yet, every tick.

Why? Simple, if a packet gets lost the next one carries the same inputs again, so the server never misses one. And since the ticks are consecutive we only need to send the first tick and then one byte per input.
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
Most of what arrives is inputs the server already has, so it only keeps the ones newer than the last input it received and puts them in a queue.
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
> The cap at the end is important. Inputs arrive at 1 per tick and get processed at 1 per tick, so if a pile of them builds up (for example the client comes back from a lag spike with a second of inputs) it would never go down, and that second would stay as input delay forever. So past `MaxQueuedInputs` the oldest ones get thrown away, the client reconciles once and it's back to real time

#### Processing them
The inputs are not processed the moment they arrive. They're processed in the server's tick, one per tick, the same way the client made them.

If the server just ran everything it received, a modified client could send 2 inputs per tick and move at double speed. So each player gets a budget of one input per tick. It can save up a few (`MaxInputBudget`) to catch up after jitter or a lost packet, but on average nobody moves faster than real time. Source engine does the same thing with `sv_maxusrcmdprocessticks`.
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
After simulating, the server sends the resulting state back with the tick of the last input it processed. That tick is how the client knows which of its predictions to compare.

## 6) Reconciliation
When a state arrives from the server the client just saves it. Unreliable messages can arrive late, twice or out of order, so anything older than what we already have gets ignored.
```cs
private void HandleServerState(SimulationState serverState)
{
    if (serverState.tick <= lastAckedTick) return;

    latestServerState = serverState;
    lastAckedTick = serverState.tick;
    hasNewServerState = true;
}
```
Then at the start of the next tick we check it
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
> I trimmed this one, the real function also handles a server state older than our whole history and the visual smoothing below

We compare what we predicted for that tick with what the server says. If they match we were right and there's nothing to do. If not, we apply the server's state and process every input from the tick after it up to now again, replacing the saved states as we go so the next comparison uses the corrected ones.

There's one Unity specific problem here
```cs
private void ApplySimulationState(SimulationState state)
{
    transform.position = state.position;
    velocity = state.velocity;
    grounded = state.grounded;

    Physics.SyncTransforms();
}
```
`CharacterController` keeps its own copy of the position. If you set `transform.position` and call `Move()` right after without `Physics.SyncTransforms()`, the move starts from where the controller thinks it is, so the whole replay is wrong. This was one of the reasons the replay "broke" in the old version.

#### Smoothing the corrections
The simulation snaps to the corrected position right away, but the mesh doesn't have to. The mesh is a child called `Visual`. It gets interpolated between the last two ticks so it looks smooth at any framerate, and when a correction happens the difference is saved as an offset that goes away over a few frames.
```cs
private void Update()
{
    visualCorrectionOffset = Vector3.Lerp(visualCorrectionOffset, Vector3.zero, 1f - Mathf.Exp(-movementSettings.correctionSmoothing * Time.deltaTime));

    float tickFraction = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
    visual.position = Vector3.Lerp(previousTickPosition, transform.position, tickFraction) + visualCorrectionOffset;
}
```
> Corrections bigger than `maxSmoothedCorrection` aren't smoothed, if the server teleports you it should look like a teleport

## 7) Faking a bad connection
Testing prediction on localhost doesn't tell you much because there's no ping. So everything the game sends goes through `SendToServer` / `SendToClient` on the `NetworkManager`. These hold each message for half the simulated ping (± jitter) and randomly drop the unreliable ones.
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
> In your actual game you can just call `Client.Send` / `Server.Send` directly. Reliable messages never get dropped here, Riptide would just resend them anyway

## 8) Conclusion
That's all there is to client side prediction.
In short, the client saves its inputs and predicted states with a tick and moves right away, before the inputs even reach the server.
Every tick it sends all the inputs the server hasn't confirmed yet, so a lost packet doesn't matter.
The server processes them in its own tick and sends the result back with the tick of the last input.
The client compares that with what it predicted for the same tick, and if it doesn't match it goes back to the server's state and processes its inputs again.

# What was wrong with the old version
If you used the old version, these are the bugs it had:
- The replay was commented out. Reconciliation snapped the position but never processed the inputs again. It "broke" because:
  - Velocity wasn't saved in the state, so gravity and jumps were replayed from the wrong velocity
  - The `CharacterController` wasn't synced after the teleport (the `Physics.SyncTransforms()` above)
  - The state was saved before applying the input but the server sent the state after it, so every comparison was off by one tick
- The server skipped the newest input in every message (`i < inputs.Length - 1`), so it was always one tick behind
- The input of the current tick wasn't sent, it only went out on the next tick
- The number of redundant inputs was a `byte` with no limit. If the server didn't answer for 256+ ticks it overflowed and the message was garbage
- Ticks were `ushort`, so every "is this newer" check broke once the tick wrapped around
- Movement ignored delta time. Speed and gravity were applied per tick, so changing the tick rate changed how fast you moved
- The fake ping didn't really work. It used `Invoke` to delay calling `SendInput`, which then read the inputs at the moment it ran, not when they were made
- The server trusted how many inputs the client sent, so a modified client could speedhack

# Going further
This only covers the local player. In an actual game you also need:
- Other players. You don't predict them, you interpolate them between the states the server sends, a bit in the past. That's called snapshot interpolation
- Lag compensation. Since everyone sees the other players in the past, the server has to rewind them when it checks if a shot hit
- Time dilation. The server here never makes up inputs, if the queue is empty the player just waits for the next one. Games like Overwatch instead make the client run a bit faster or slower to keep the server's input queue at the right size

# Credits
I used these as a reference, both back then and now:
- [Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/) - Glenn Fiedler
- [Fast-Paced Multiplayer](https://www.gabrielgambetta.com/client-server-game-architecture.html) - Gabriel Gambetta
- [Source Multiplayer Networking](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking) - Valve
- [Overwatch Gameplay Architecture and Netcode](https://www.youtube.com/watch?v=W3aieHjyNvw) - Timothy Ford, GDC 2017
- [Client-Side Prediction With Physics In Unity](https://www.codersblock.org/blog/client-side-prediction-in-unity-2018)
- [Seamless fast paced multiplayer in Unity3D](https://medium.com/@christian.tucker_68732/seamless-fast-paced-multiplayer-in-unity3d-implementing-client-side-prediction-ab520bf49bd1)

I'm not a native English speaker, so if there's anything wrong or missing feel free to contact me.
