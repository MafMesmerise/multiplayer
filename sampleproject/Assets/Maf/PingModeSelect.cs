using UnityEngine;
using UnityEngine.SceneManagement;

public class PingModeSelect : MonoBehaviour
{
    private void OnGUI()
    {
        if (GUILayout.Button("Server"))
        {
            SceneManager.LoadScene("PingServer");
        }
        if (GUILayout.Button("Client"))
        {
            SceneManager.LoadScene("PingClient");
        }
    }
}
