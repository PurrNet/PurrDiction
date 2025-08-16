using PurrNet;
using UnityEngine;
using UnityEngine.InputSystem;

public class TestScript : MonoBehaviour
{
    void Update()
    {
        if (Keyboard.current.oKey.wasPressedThisFrame)
            NetworkManager.main.sceneModule.LoadSceneAsync("Scene2");

        if (Keyboard.current.pKey.wasPressedThisFrame)
        {
            NetworkManager.main.StopClient();
            NetworkManager.main.StopServer();
        }
    }
}
