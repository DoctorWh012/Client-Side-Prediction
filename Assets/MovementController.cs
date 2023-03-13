using System.Collections;
using System;
using System.Collections.Generic;
using UnityEngine;
using Riptide;

public class SimulationState
{
    public Vector3 position;

    public ushort currentTick;
}

public class ClientInputState
{
    public float horizontal = 0;
    public float vertical = 0;
    public bool jump = false;

    public ushort currentTick = 0;
}

public class MovementController : MonoBehaviour
{
    public ushort cspTick { get; private set; }
    public const int StateCacheSize = 1024;
    [Header("Components")]
    [SerializeField] private SimplePlayerMovement movement;

    // Client Side prediction stuff
    private SimulationState[] simulationStateCache = new SimulationState[StateCacheSize];
    private ClientInputState[] inputStateCache = new ClientInputState[StateCacheSize];
    public SimulationState serverSimulationState = new SimulationState();
    private int lastCorrectedFrame;

    private float timer;
    private float minTimeBetweenTicks;

    private void Start()
    {
        minTimeBetweenTicks = 1f / NetworkManager.Singleton.ServerTickRate; // Calculate the minTimeBetweenTicks using the serverTickRate defined on the network manager
    }

    private void Update()
    {
        // This creates a fixed timestep to keep the server and the client syncronized
        timer += Time.deltaTime;
        while (timer >= minTimeBetweenTicks)
        {
            timer -= minTimeBetweenTicks;

            // Gets the cache index 
            // This works like, if its tick 300 cache index is 300 but when it gets past the cachesize it resets
            // So when its tick 1025 cacheIndex is 1
            int cacheIndex = cspTick % StateCacheSize;

            // Gets the Inputs and stores them in a cache
            inputStateCache[cacheIndex] = GetInput();

            // Gets the current Sim State and Stores them in a cache
            simulationStateCache[cacheIndex] = CurrentSimulationState();

            // Runs the movement on the client before sending it to the server
            movement.SetInput(inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].jump);

            // Sends the inputs to the server
            // Here i doing a probabiity check to simulate packet loss and also im using invoke with a delay to simulate high ping
            // This should not be on your actual code, you can just call it directly
            if (NetworkManager.Singleton.packetLossChance < UnityEngine.Random.Range(0, 100))
            {
                Invoke("SendInput", NetworkManager.Singleton.inputMessageDelay / 100);
            }
            cspTick++;
        }

        // If there an available ServerState it will check if reconciliation is needed
        if (serverSimulationState != null) Reconciliate();
    }

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

    private void Reconciliate()
    {
        // Makes sure that the ServerSimState is not outdated
        if (serverSimulationState.currentTick <= lastCorrectedFrame) return;

        int cacheIndex = serverSimulationState.currentTick % StateCacheSize;

        ClientInputState cachedInputState = inputStateCache[cacheIndex];
        SimulationState cachedSimulationState = simulationStateCache[cacheIndex];

        // Find the difference between the Server Player Pos And the Client predicted Pos
        float posDif = Vector3.Distance(cachedSimulationState.position, serverSimulationState.position);

        // A correction is necessary.
        if (posDif > 0.001f)
        {
            Debug.LogError("Needed reconciliation");
            // Set the player's position to match the server's state. 
            transform.position = serverSimulationState.position;

            // Declare the rewindFrame as we're about to resimulate our cached inputs. 
            ushort rewindTick = serverSimulationState.currentTick;

            // Loop through and apply cached inputs until we're 
            // caught up to our current simulation frame. 
            while (rewindTick < cspTick)
            {
                // Determine the cache index 
                int rewindCacheIndex = rewindTick % StateCacheSize;

                // Obtain the cached input and simulation states.
                ClientInputState rewindCachedInputState = inputStateCache[rewindCacheIndex];
                SimulationState rewindCachedSimulationState = simulationStateCache[rewindCacheIndex];

                // If there's no state to simulate, for whatever reason, 
                // increment the rewindFrame and continue.
                if (rewindCachedInputState == null || rewindCachedSimulationState == null)
                {
                    ++rewindTick;
                    continue;
                }

                // Process the cached inputs. 
                // You are supposed to process the cached inputs but for some reason it's breaking the reconciliation
                
                // movement.SetInput(rewindCachedInputState.vertical, rewindCachedInputState.horizontal, rewindCachedInputState.jump); 

                // Replace the simulationStateCache index with the new value.
                simulationStateCache[rewindCacheIndex] = CurrentSimulationState();
                simulationStateCache[rewindCacheIndex].currentTick = rewindTick;

                // Increase the amount of frames that we've rewound.
                ++rewindTick;
            }
        }
        // Once we're complete, update the lastCorrectedFrame to match.
        // NOTE: Set this even if there's no correction to be made. 
        lastCorrectedFrame = serverSimulationState.currentTick;
    }

    private void SendInput()
    {
        Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.input);

        // First let's send the size of the list of Redundant messages being sent to the server
        // As we send the inputs starting from the last received server tick until our current tick
        // The quantity of message is going to be currentTick - lastReceived tick
        message.AddByte((byte)(cspTick - serverSimulationState.currentTick));

        // print($"Trying to send {(cspTick - serverSimulationState.currentTick)} messasges to the server");

        // // Sends all the messages starting from the last received server tick until our current tick
        for (int i = serverSimulationState.currentTick; i < cspTick; i++)
        {
            message.AddFloat(inputStateCache[i % StateCacheSize].horizontal);
            message.AddFloat(inputStateCache[i % StateCacheSize].vertical);
            message.AddBool(inputStateCache[i % StateCacheSize].jump);
            message.AddUShort(inputStateCache[i % StateCacheSize].currentTick);
        }
        NetworkManager.Singleton.Client.Send(message);
    }

    [MessageHandler((ushort)ServerToClientId.movement)]
    private static void Movement(Message message)
    {
        // When we receive the processed movement back from the server we save it
        // We have to also verify that the received movement is newer than the one we last received
        ushort serverMovementTick = message.GetUShort();
        Vector3 serverPlayerPos = message.GetVector3();
        if (serverMovementTick > PlayerManager.Instance.clientMovementController.serverSimulationState.currentTick)
        {
            PlayerManager.Instance.clientMovementController.serverSimulationState.position = serverPlayerPos;
            PlayerManager.Instance.clientMovementController.serverSimulationState.currentTick = serverMovementTick;
        }

        // print($"Received tick =>{serverMovementTick} and position => {serverPlayerPos}");
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.blue;
        Gizmos.DrawWireSphere(serverSimulationState.position, 1f);
    }
}
