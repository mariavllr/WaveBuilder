using UnityEngine;

public static class SaveSystem
{
    private const string PlayerDataKey = "PLAYER_DATA";

    public static void SavePlayerData(PlayerData data)
    {
        string json = JsonUtility.ToJson(data);
        PlayerPrefs.SetString(PlayerDataKey, json);
        PlayerPrefs.Save();
    }

    public static PlayerData LoadPlayerData()
    {
        if (PlayerPrefs.HasKey(PlayerDataKey))
        {
            string json = PlayerPrefs.GetString(PlayerDataKey);
            return JsonUtility.FromJson<PlayerData>(json);
        }

        return new PlayerData(); // Nuevo si no existe
    }
}
