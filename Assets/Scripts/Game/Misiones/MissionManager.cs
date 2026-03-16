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

    private void OnEnable()
    {
        GameEvents.OnMissionCompleted += OnMissionCompleted;
    }

    private void OnDisable()
    {
        GameEvents.OnMissionCompleted -= OnMissionCompleted;
    }

    public void InitializeMissions(LevelData levelData)
    {
        activeMissions.Clear();
        //Para cada mision del nivel
        foreach (var data in levelData.missions)
        {
            Mission newMission = null;

            switch (data.type)
            {
                case MissionType.PlaceTile:
                    newMission = new Mission_PlaceTileType(data, data.targetTileTypes[0]);
                    break;
                case MissionType.TilesTogether:
                    newMission = new Mission_TilesTogether(data, data.targetTileTypes, data.amountRequired);
                    break;
            }

            if (newMission != null)
            {
                activeMissions.Add(newMission);
                newMission.StartListening();
                AddMissionToUI(data);
            }
        }
    }

    private void AddMissionToUI(MissionData missionData)
    {
        GameObject missionUI = Instantiate(missionUIPrefab, missionsContainerUI.transform);
        missionUI.GetComponent<TextMeshProUGUI>().text = missionData.missionDescription;
        missionUI.name = missionData.missionID.ToString();
    }

    public void OnMissionCompleted(MissionData mission)
    {
        //Borrar mision de la UI
        foreach (Transform child in missionsContainerUI.transform)
        {
            if (child.name == mission.missionID.ToString())
            {
                Destroy(child.gameObject);
                break;
            }
        }

        // Actualizar progreso del jugador
        //SaveSystem.Instance.MarkMissionAsCompleted(mission.data.missionID);

        // Limpia la memoria
       // activeMissions.Remove(mission);
    }
}
