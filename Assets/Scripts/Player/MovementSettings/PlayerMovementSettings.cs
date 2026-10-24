using UnityEngine;

[CreateAssetMenu(fileName = "PlayerMovement", menuName = "Client Side Prediction/PlayerMovement", order = 0)]
public class PlayerMovementSettings : ScriptableObject
{
    [Header("Movement")]
    [Space(5)]
    [SerializeField] public float moveSpeed = 6f;
    [SerializeField] public float gravity = 25f;
    [SerializeField] public float jumpHeight = 1.5f;

    [Header("Reconciliation")]
    [Space(5)]
    [SerializeField] public float reconciliationThreshold = 0.001f; // Anything above float noise is a misprediction

    [SerializeField] public float correctionSmoothing = 12f;
    [SerializeField] public float maxSmoothedCorrection = 2f; // Bigger corrections just snap
}
