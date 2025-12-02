using UnityEngine;

public class LevelManager : MonoBehaviour
{
    public LevelData currentLevel;

    private void Start()
    {
        MissionManager.Instance.InitializeMissions(currentLevel);
    }
}
