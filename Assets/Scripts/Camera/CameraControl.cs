using System.Collections;
using UnityEngine;

public class CameraControl : MonoBehaviour
{
    public float rotationSpeed = 400f;
    private bool isRotating = false;

    [Header("Camera")]
    public Camera cam;
    public Vector3 cameraLocalPosition; // posición local de la cámara respecto al pivot
    public float sizePerCell = 0.8f;    // cuánto size ortográfico por celda

    // Evento: +1 si cámara gira 90° antihorario (RotateLeft), -1 si horario (RotateRight)
    public delegate void OnCameraRotated(int steps);
    public static event OnCameraRotated onCameraRotated;

    public void SetupCamera(int dimensionsX, int dimensionsZ, int dimensionsY, int cellSize)
    {
        float centerX = (dimensionsX - 1) * cellSize / 2f;
        float centerZ = (dimensionsZ - 1) * cellSize / 2f;
        transform.position = new Vector3(centerX, 0, centerZ);

        float maxDimension = Mathf.Max(dimensionsX, dimensionsZ);
        if (cam != null)
            cam.orthographicSize = maxDimension * cellSize * sizePerCell;
    }

    public void RotateLeft()
    {
        if (!isRotating)
        {
            onCameraRotated?.Invoke(+1);
            StartCoroutine(RotateToAngle(90));
        }
    }

    public void RotateRight()
    {
        if (!isRotating)
        {
            onCameraRotated?.Invoke(-1);
            StartCoroutine(RotateToAngle(-90));
        }
    }

    private IEnumerator RotateToAngle(float angle)
    {
        isRotating = true;
        Quaternion startRot = transform.rotation;
        Quaternion endRot = Quaternion.Euler(0, transform.eulerAngles.y + angle, 0);
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime * (rotationSpeed / 90f);
            transform.rotation = Quaternion.Slerp(startRot, endRot, t);
            yield return null;
        }
        transform.rotation = endRot;
        isRotating = false;
    }
}