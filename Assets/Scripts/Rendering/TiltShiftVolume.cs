using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Override de Volume para el efecto tilt-shift (banda horizontal nítida, desenfoque arriba y abajo).
// Se añade desde el Volume Profile: Add Override > Post-processing Custom > Tilt Shift
[Serializable, VolumeComponentMenu("Post-processing Custom/Tilt Shift")]
[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
public sealed class TiltShiftVolume : VolumeComponent
{
    [Tooltip("Intensidad global del efecto. 0 = desactivado.")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);

    [Tooltip("Posición vertical del centro de la banda enfocada (0 = abajo, 1 = arriba).")]
    public ClampedFloatParameter focusCenter = new ClampedFloatParameter(0.5f, 0f, 1f);

    [Tooltip("Media altura de la zona totalmente nítida (en fracción de pantalla).")]
    public ClampedFloatParameter focusWidth = new ClampedFloatParameter(0.12f, 0f, 0.5f);

    [Tooltip("Distancia de transición entre nítido y desenfocado.")]
    public ClampedFloatParameter falloff = new ClampedFloatParameter(0.25f, 0.001f, 1f);

    [Tooltip("Separación entre muestras del blur (en texels de la textura reducida).")]
    public ClampedFloatParameter blurRadius = new ClampedFloatParameter(1.5f, 0f, 6f);

    [Tooltip("Número de pasadas de blur. Más pasadas = desenfoque más amplio y suave.")]
    public ClampedIntParameter iterations = new ClampedIntParameter(2, 1, 6);

    [Tooltip("Factor de reducción de resolución para el blur (2 = mitad).")]
    public ClampedIntParameter downsample = new ClampedIntParameter(2, 1, 4);

    [Tooltip("Muestra la máscara de enfoque (blanco = desenfocado) para ajustar la banda.")]
    public BoolParameter debugMask = new BoolParameter(false);

    public bool IsActive() => active && intensity.value > 0f;
}
