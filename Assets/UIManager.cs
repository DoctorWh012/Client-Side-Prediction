using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class UIManager : MonoBehaviour
{
    [Header("Components")]
    [SerializeField] private TextMeshProUGUI pingDisplay;

    void Update()
    {
        pingDisplay.SetText($"Ping: {NetworkManager.Singleton.Client.RTT.ToString()}");
    }
}
