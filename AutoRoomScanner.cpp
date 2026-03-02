using UnityEngine;

public class AutoRoomScanner : MonoBehaviour
{
    private OVRSceneManager sceneManager;

    void Start()
    {
        sceneManager = FindObjectOfType<OVRSceneManager>();

        if (sceneManager == null)
        {
            Debug.LogError("場景中沒有 OVRSceneManager！");
            return;
        }

        // 要求進行空間掃描
        sceneManager.RequestSceneCapture();
    }
}