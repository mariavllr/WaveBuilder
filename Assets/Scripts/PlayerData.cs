using System.Collections.Generic;


//Puntos por CADA NIVEL
[System.Serializable]
public class LevelProgress
{
    public int levelID; // Coincide con el levelID del LevelData
    public int score;
    public bool isCompleted;
    public List<MissionProgress> missionProgressList = new List<MissionProgress>();
}

[System.Serializable]
public class MissionProgress
{
    public int missionID;
    public bool isCompleted;
}

//Datos guardados del jugador
[System.Serializable]
public class PlayerData
{
    public List<LevelProgress> levelProgressList = new List<LevelProgress>(); //Para saber los puntos del jugador por nivel, buscas por levelID
}

