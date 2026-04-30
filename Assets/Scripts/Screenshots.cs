using UnityEngine;
using System.IO;


public class Screenshots : MonoBehaviour
{
    string timestamp;
    string filePath;
    string folderPath;
    private void Start()
    {
        folderPath = Application.dataPath + "/Screenshots";
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.S))
        {
            timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            filePath = folderPath + "/WaveBuilderScreenshot_" + timestamp + ".png";

            ScreenCapture.CaptureScreenshot(filePath);
            Debug.Log(filePath + "saved at Screenshots.");
        }
    }
}
