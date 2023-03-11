using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager Instance;

    [Header("Components")]
    [SerializeField] public SimplePlayerMovement serverPlayerMovement;
    [SerializeField] public SimplePlayerMovement clientServerMovement;
    [SerializeField] public MovementController clientMovementController;

    // This Script is merely a workaround to allow me to access Scripts inside messageHandlers without a proper player class
    private void Awake()
    {
        Instance = this;
    }
}
