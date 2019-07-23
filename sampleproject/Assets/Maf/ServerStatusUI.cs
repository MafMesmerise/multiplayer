using UnityEngine;
using UnityEngine.UI;

public class ServerStatusUI : MonoBehaviour
{
    public static ServerStatusUI instance;

    public Text txtStatus;

    private void Awake()
    {
        instance = this;
    }

    public void UpdateStatus(string status)
    {
        txtStatus.text = status;
    }
}
