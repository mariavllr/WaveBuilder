using System;

public abstract class Mission
{
    public MissionData data;
    public bool isCompleted = false;

    public Mission(MissionData data)
    {
        this.data = data;
    }

    public abstract void StartListening();
    public abstract void StopListening();

    public virtual void Complete()
    {
        if (isCompleted) return;
        isCompleted = true;

        UnityEngine.Debug.Log($"Misión completada: {data.missionName}");
        StopListening();

        MissionManager.Instance.OnMissionCompleted(this);
    }
}

