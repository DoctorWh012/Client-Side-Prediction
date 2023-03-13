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
