using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Riptide;
using Riptide.Utils;

public enum ServerToClientId : ushort
{
    movement = 1,
}

public enum ClientToServerId : ushort
{
    input = 1,
}


public class NetworkManager : MonoBehaviour
{
    private static NetworkManager _singleton;

    public static NetworkManager Singleton
    {
        get => _singleton;
        private set
        {
            if (_singleton == null)
            {
                _singleton = value;
            }

            else if (_singleton != value)
            {
                Debug.Log($"{nameof(NetworkManager)} instance already exists, destroying duplicate");
                Destroy(value.gameObject);
            }

        }
    }

    public Client Client { get; private set; }
    public Server Server { get; private set; }

    public float ServerTickRate = 60f;
    [Header("Server Settings")]
    [SerializeField] private float macClientCount;

    [Header("Client Side Prediction")]
    [SerializeField] public float inputMessageDelay;
    [SerializeField] public float packetLossChance;

    private void Awake()
    {
        Singleton = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        Server = new Server();
        Server.Start(8989, 2);
        RiptideLogger.Initialize(Debug.Log, Debug.Log, Debug.LogWarning, Debug.LogError, false);
        Client = new Client();
        Client.Connect("127.0.0.1:8989");
    }


    private void FixedUpdate()
    {
        if (Server.IsRunning) Server.Update();
        Client.Update();
    }
}
