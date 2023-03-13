# Client-Side-Prediction
A step by step guide on how to do client side prediction with non physics based movement

# What is client side prediction?
Client side prediction is allowing the client to predict it's own movement in an authoritative server enviroment
It is used for hiding the latency from the server receiving the input and sending it back to the player

# Server reconciliation 
Sometimes the player's position may dirft from the position on the server, As our enviroment is server authoritative we always trust the server
This means snaping the player to the position received from the server and processing the inputs again resulting in a prediction of what the correct position is

# Requirements
In this demo i am using [Riptide Networking](https://riptide.tomweiland.net/manual/overview/about-riptide.html) but you can use any networking implementation

This implementation will only work with deterministic movement, this means no RigidBody movement although i am working on a version that will work with RigidBodies

# How to
Now let's go step by step on how to implement client side prediction

## 1) Setting a fixed timestep
Instead of using fixed update we are going to set a fixed timestep, the reason for this can be read in this [Article](https://gafferongames.com/post/fix_your_timestep/)

In the NetworkManager we assign our desired TickRate  
```cs
public float ServerTickRate = 60f;
```  

The TickRate is then used on the MovementController to create a Fixed TimeStep
> Note that you have to create two `float` variables `timer` and `minTimeBetweenTicks`

```cs
private void Start()
    {
        minTimeBetweenTicks = 1f / NetworkManager.Singleton.ServerTickRate;
    }
```

```cs
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
    }
}
```

## 2) Creating the caches
Now that we have a fixed TimeStep we can create our caches, they will be used to save previous player inputs and positions so we can 
compare to the ones received from the server in the same tick.  
  
We will need a `const int` called StateCacheSize, this will determine the maximum amount of previous Inputs/Positions saved  
```cs
public const int StateCacheSize = 1024;
```  
  
Now we create two classes one for the `InputState` and another one for `SimulationState`
>You can use `struct` if you want, but you will need to change some of the code
```cs
public class SimulationState
{
    public Vector3 position;

    public ushort currentTick;
}

public class ClientInputState
{
    public float horizontal;
    public float vertical;
    public bool jump;

    public ushort currentTick;
}
```
As you can see each of these classes contain a `currentTick` variable, this is so we can tell from when that cachedState is.  

Now we create two arrays to hold both states
```cs
private SimulationState[] simulationStateCache = new SimulationState[StateCacheSize];
private ClientInputState[] inputStateCache = new ClientInputState[StateCacheSize];
```
> As you can see the size of these arrays is defined by the cacheSize  

We also need a way to tell our current Tick, so we create another 'ushort'  variable  
```cs
public ushort cspTick { get; private set; }
```
> I named this variable as cspTick but feel free to name it as currentTick if it makes more sense to you  

## 3) Logic
First we increment our `cspTick` on our fixed TimeStep  
```cs
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        cspTick++;
    }
}
```
  
Now let's get and save the player inputState and simulationState
```cs
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        int cacheIndex = cspTick % StateCacheSize;

        inputStateCache[cacheIndex] = GetInput();
        simulationStateCache[cacheIndex] = CurrentSimulationState();
            
        cspTick++;
    }
}
```
> You can see we use two functions these are
```cs
private ClientInputState GetInput()
{
    return new ClientInputState
    {
        vertical = Input.GetAxisRaw("Vertical"),
        horizontal = Input.GetAxisRaw("Horizontal"),
        jump = Input.GetKey(KeyCode.Space),
        currentTick = cspTick
    };
}

private SimulationState CurrentSimulationState()
{
    return new SimulationState
    {
        position = transform.position,
        currentTick = cspTick
    };
}
```
> They simply return the current inputState and simulationState with it's corresponding tick  

You can also notice the use of a `cacheIndex` what it does is that it serves as an index for where to save the current Simulation/Inputs on the arrays and if the `cspTick` 
goes over the cache size it starts from the beginning

Now that we have cached our inputs and simulation state we can some actual client side prediction  
Which mean we are going to process the result of the inputs before sending them to the server  
```cs 
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        int cacheIndex = cspTick % StateCacheSize;

        inputStateCache[cacheIndex] = GetInput();
        simulationStateCache[cacheIndex] = CurrentSimulationState();
    
        movement.SetInput(inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].jump);

        cspTick++;
    }
}
```
> As you can see we have a reference to a script called movement, Movement controller is responsible for getting and sending the player input to the server as well as doing server reconciliation.  
The movement script is responsible for getting the player input and moving the player as well as sending the result of the input back to the client, the movementController should run only on the localPlayer and the movement should run both in the local and netPlayer

We will have a look on the movement script soon but first let's first send the inputs to the server  
```cs
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        int cacheIndex = cspTick % StateCacheSize;

        inputStateCache[cacheIndex] = GetInput();
        simulationStateCache[cacheIndex] = CurrentSimulationState();
    
        movement.SetInput(inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].jump);

        SendInput();

        cspTick++;
    }
}
```
```cs
    private void SendInput()
    {
        Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.input);

        message.AddByte((byte)(cspTick - serverSimulationState.currentTick));

        for (int i = serverSimulationState.currentTick; i < cspTick; i++)
        {
            message.AddFloat(inputStateCache[i % StateCacheSize].horizontal);
            message.AddFloat(inputStateCache[i % StateCacheSize].vertical);
            message.AddBool(inputStateCache[i % StateCacheSize].jump);
            message.AddUShort(inputStateCache[i % StateCacheSize].currentTick);
        }
        NetworkManager.Singleton.Client.Send(message);
    }
```
> As you can see sending the inputs to the server is not as simple as just sending the currentInput, what we are doing here is that we are sending Redundant inputs to the server, so we send all cached inputs starting from the last received StateTick from the server.  
> 
> Why? 
> Simple this prevents the server from missing any inputs if some packet gets lost over the Net.

for this demo i am sending floats for the vertical/horizontal input, but try to keep the size of the message as small as possible this means using the lowest possible number of bytes for each variable


For now this is it before we tackle the Server Reconciliation

#### Movement Script
In the movement script we create a `SetInput` function
```cs
public void SetInput(float ver, float hor, bool jmp)
{
    vertical = ver;
    horizontal = hor;
    jump = jmp;

    HandleTick();
}
```

In this function we reference `HandleTick` which is where i apply my movement logic
```cs
private void HandleTick()
{
    groundedPlayer = controller.isGrounded;
    if (groundedPlayer && playerVelocity.y < 0)
    {
        playerVelocity.y = 0f;
    }

    Vector3 move = new Vector3(horizontal, 0, vertical);
    controller.Move(move * playerSpeed);
    if (move != Vector3.zero)
    {
    gameObject.transform.forward = move;
    }

    if (jump && groundedPlayer)
    {
    playerVelocity.y += Mathf.Sqrt(jumpHeight * -3.0f * gravityValue);
    }

    playerVelocity.y += gravityValue;
    controller.Move(playerVelocity);
}
```
> In this demo for the movement i am using Unity's Character controller [example movement](https://docs.unity3d.com/ScriptReference/CharacterController.Move.html), but feel free to use your own movement code, just have in mind that the same movement logic has to be applied on the server and client

We also need to receive the inputs sent by the client
```cs
[MessageHandler((ushort)ClientToServerId.input)]
private static void Input(ushort fromClientId, Message message)
{
    byte inputsQuantity = message.GetByte();
    ClientInputState[] inputs = new ClientInputState[inputsQuantity];

    for (int i = 0; i < inputsQuantity; i++)
    {
        inputs[i] = new ClientInputState
        {
            horizontal = message.GetFloat(),
            vertical = message.GetFloat(),
            jump = message.GetBool(),
            currentTick = message.GetUShort()
        };
    }

    PlayerManager.Instance.serverPlayerMovement.HandleClientInput(inputs);
    }   
```
>Here we use Riptide's `MessageHandler` to get the input message then we create an array to save the inputs and set it's size to the `inputQuatity`, after that we just loop in order to get all the messages that the client sent.  
In  this demo i do not have a player class so i just use a placeholder  to get access to the `serverPlayerMovement` script

Handling the client's input
```cs
    private void HandleClientInput(ClientInputState[] inputs)
    {
    if (!serverPlayer || inputs.Length == 0) return;
    if (inputs[inputs.Length - 1].currentTick >= lastReceivedInputs.currentTick)
    {
        int start = lastReceivedInputs.currentTick > inputs[0].currentTick ? (lastReceivedInputs.currentTick - inputs[0].currentTick) : 0;

        for (int i = start; i < inputs.Length - 1; i++)
        {
            SetInput(inputs[i].vertical, inputs[i].horizontal, inputs[i].jump);
        }
        lastReceivedInputs = inputs[inputs.Length - 1];
        SendMovement();
    }
}
```

--------To Be Finished----------