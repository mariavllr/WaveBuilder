using UnityEngine;
using DG.Tweening;
using TMPro;

public class DT_MissionMessage : MonoBehaviour
{
    private RectTransform rect;
    private Vector2 hiddenPos;      // Posición fuera de pantalla
    private Vector2 shownPos;       // Posición visible en pantalla


    [Header("Opciones")]
    public float screenOffset = 20f;     // separación opcional del borde derecho
    public float moveDuration = 0.4f;
    public float visibleTime = 2f;

    [Header("UI")]
    public TextMeshProUGUI messageText;

    private void OnEnable()
    {
        GameEvents.OnMissionCompleted += ShowMissionCompletedMessage;
        GameEvents.OnScoreUpdated += ShowUpdatedScoreMessage;
    }

    private void OnDisable()
    {
        GameEvents.OnMissionCompleted -= ShowMissionCompletedMessage;
        GameEvents.OnScoreUpdated -= ShowUpdatedScoreMessage;
    }

    void Awake()
    {
        rect = GetComponent<RectTransform>();

        float width = rect.rect.width;
        hiddenPos = new Vector2(width, 0f);
        shownPos = new Vector2(-screenOffset, 0f);


        rect.anchoredPosition = hiddenPos;
    }

    public void ShowMissionCompletedMessage(MissionData data)
    {
        messageText.text = data.missionDescription + " +" + data.givenPoints;
        ShowMessage();
    }

    public void ShowUpdatedScoreMessage(int givenPoints)
    {
        messageText.text = "+ " + givenPoints;
        ShowMessage();
    }

    private void ShowMessage()
    {
        rect.DOKill();  // Cancelamos cualquier animación previa

        Sequence seq = DOTween.Sequence();

        seq.Append(rect.DOAnchorPos(shownPos, moveDuration).SetEase(Ease.OutCubic));  // Mostrar
        seq.AppendInterval(visibleTime);                                              // Esperar
        seq.Append(rect.DOAnchorPos(hiddenPos, moveDuration).SetEase(Ease.InCubic));  // Ocultar
    }
}