using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;

public class MissionManager : MonoBehaviour
{
    public static MissionManager Instance;
    public GameObject missionsContainerUI;
    public GameObject missionUIPrefab;

    private List<Mission> activeMissions = new List<Mission>();

    private void Awake()
    {
        Instance = this;
    }

    public void InitializeMissions(LevelData levelData)
    {
        activeMissions.Clear();

        foreach (var missionData in levelData.missions)
        {
            // EJEMPLO: si es una misión de colocar el campamento
            if (missionData.missionID == 0)
            {
                var mission = new Mission_PlaceTileType(missionData, "campfire");
                activeMissions.Add(mission);
                mission.StartListening();
                AddMissionToUI(missionData);
            }
            else if (missionData.missionID == 1)
            {
                string[] pineTypes = { "pine", "pines", "pineAutumn" };
                var mission = new Mission_TilesTogether(missionData, pineTypes, 3);
                activeMissions.Add(mission);
                mission.StartListening();
                AddMissionToUI(missionData);
            }

            else if (missionData.missionID == 2)
            {
                string[] town = { "campfire" };
                var mission = new Mission_TilesTogether(missionData, town, 4);
                activeMissions.Add(mission);
                mission.StartListening();
                AddMissionToUI(missionData);
            }


        }
    }

    private void AddMissionToUI(MissionData missionData)
    {
        GameObject missionUI = Instantiate(missionUIPrefab, missionsContainerUI.transform);
        missionUI.GetComponent<TextMeshProUGUI>().text = missionData.missionDescription;
        missionUI.name = missionData.missionID.ToString();
    }

    public void OnMissionCompleted(Mission mission)
    {
        // Mostrar mensaje en pantalla
        //Debug.Log("Misión completada: " + mission.data.missionName);
        GameEvents.MissionCompleted(mission.data);

        //Borrar mision de la UI
        foreach (Transform child in missionsContainerUI.transform)
        {
            if (child.name == mission.data.missionID.ToString())
            {
                Destroy(child.gameObject);
                break;
            }
        }

        // Actualizar progreso del jugador
        //SaveSystem.Instance.MarkMissionAsCompleted(mission.data.missionID);
    }
}
