using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Riptide;

public class SimplePlayerMovement : MonoBehaviour
{
    [Header("Components")]
    [SerializeField] private CharacterController controller;

    [Header("Settings")]
    [SerializeField] private float playerSpeed = 2.0f;
    [SerializeField] private float jumpHeight = 1.0f;
    [SerializeField] private float gravityValue = -9.81f;

    [SerializeField] private bool serverPlayer;

    private ClientInputState lastReceivedInputs = new ClientInputState();
    private bool groundedPlayer;
    private Vector3 playerVelocity;
    private float vertical;
    private float horizontal;
    private bool jump;

    public void SetInput(float ver, float hor, bool jmp)
    {
        vertical = ver;
        horizontal = hor;
        jump = jmp;

        HandleTick();
    }

    private void HandleClientInput(ClientInputState[] inputs)
    {
        if (!serverPlayer || inputs.Length == 0) return;
        // Last input in the array is the newest one
        // Here we check to see if the inputs sent by the client are newer than the ones we already have received
        if (inputs[inputs.Length - 1].currentTick >= lastReceivedInputs.currentTick)
        {
            // Here we check for were to start processing the inputs
            // if the iputs we already have are newer than the first ones sent we start at their difference 
            // if not we start at the first one
            int start = lastReceivedInputs.currentTick > inputs[0].currentTick ? (lastReceivedInputs.currentTick - inputs[0].currentTick) : 0;

            // Now that we have when to start we can simply apply all relevant inputs to the player
            for (int i = start; i < inputs.Length - 1; i++)
            {
                SetInput(inputs[i].vertical, inputs[i].horizontal, inputs[i].jump);
            }

            // Now we save the client newest input
            lastReceivedInputs = inputs[inputs.Length - 1];

            // Now we send the processed player movement back to the client
            SendMovement();
        }
    }

    private void HandleTick()
    {
        // This is just the movement code for the player
        // This movement code was just taken from unity's CharacterController doccuments
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

        // Changes the height position of the player..
        if (jump && groundedPlayer)
        {
            playerVelocity.y += Mathf.Sqrt(jumpHeight * -3.0f * gravityValue);
        }

        playerVelocity.y += gravityValue;
        controller.Move(playerVelocity);
    }

    private void SendMovement()
    {
        if (!serverPlayer) return;
        Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.movement);
        message.AddUShort(lastReceivedInputs.currentTick);
        message.AddVector3(transform.position);
        NetworkManager.Singleton.Server.SendToAll(message);
    }

    [MessageHandler((ushort)ClientToServerId.input)]
    private static void Input(ushort fromClientId, Message message)
    {
        // First we get how many Inputs were sent by the client
        byte inputsQuantity = message.GetByte();
        ClientInputState[] inputs = new ClientInputState[inputsQuantity];

        // Now we loops to get all the inputs sent by the client and store them in an array 
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

        // Now that we have all the messages we can handle the client inputs
        PlayerManager.Instance.serverPlayerMovement.HandleClientInput(inputs);
    }
}