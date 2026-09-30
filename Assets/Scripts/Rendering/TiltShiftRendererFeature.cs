using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Renderer Feature de tilt-shift para URP 17 (Render Graph).
// Añadir al Universal Renderer Data (PC_Renderer) y configurar con el override TiltShiftVolume.
public class TiltShiftRendererFeature : ScriptableRendererFeature
{
    [SerializeField] private Shader shader;
    [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

    private Material material;
    private TiltShiftPass pass;

    public override void Create()
    {
        if (shader == null)
            shader = Shader.Find("Hidden/TiltShift");
        if (shader == null)
            return;

        material = CoreUtils.CreateEngineMaterial(shader);
        pass = new TiltShiftPass(material) { renderPassEvent = injectionPoint };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null)
            return;

        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        TiltShiftVolume volume = VolumeManager.instance.stack.GetComponent<TiltShiftVolume>();
        if (volume == null || !volume.IsActive())
            return;

        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(material);
    }

    private class TiltShiftPass : ScriptableRenderPass
    {
        private const int PassBlurH = 0;
        private const int PassBlurV = 1;
        private const int PassComposite = 2;

        private static readonly int TexelSizeId = Shader.PropertyToID("_TS_TexelSize");
        private static readonly int ParamsId = Shader.PropertyToID("_TS_Params");
        private static readonly int Params2Id = Shader.PropertyToID("_TS_Params2");
        private static readonly int BlurTexId = Shader.PropertyToID("_TS_BlurTex");

        private readonly Material material;

        private class BlitPassData
        {
            public TextureHandle source;
            public Material material;
            public int shaderPass;
        }

        private class CompositePassData
        {
            public TextureHandle source;
            public TextureHandle blurred;
            public Material material;
        }

        public TiltShiftPass(Material material)
        {
            this.material = material;
            requiresIntermediateTexture = true;
            profilingSampler = new ProfilingSampler("TiltShift");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            if (resourceData.isActiveTargetBackBuffer || !cameraData.postProcessEnabled)
                return;

            TiltShiftVolume volume = VolumeManager.instance.stack.GetComponent<TiltShiftVolume>();
            if (volume == null || !volume.IsActive())
                return;

            // Descriptores: resultado a resolución completa, blur a resolución reducida
            RenderTextureDescriptor fullDesc = cameraData.cameraTargetDescriptor;
            fullDesc.depthStencilFormat = GraphicsFormat.None;
            fullDesc.msaaSamples = 1;

            RenderTextureDescriptor blurDesc = fullDesc;
            int ds = Mathf.Max(1, volume.downsample.value);
            blurDesc.width = Mathf.Max(1, fullDesc.width / ds);
            blurDesc.height = Mathf.Max(1, fullDesc.height / ds);

            // Parámetros constantes durante el frame de esta cámara
            material.SetVector(TexelSizeId, new Vector4(1f / blurDesc.width, 1f / blurDesc.height, blurDesc.width, blurDesc.height));
            material.SetVector(ParamsId, new Vector4(
                volume.focusCenter.value,
                volume.focusWidth.value,
                volume.falloff.value,
                volume.blurRadius.value));
            material.SetVector(Params2Id, new Vector4(
                volume.intensity.value,
                volume.debugMask.value ? 1f : 0f,
                0f, 0f));

            TextureHandle source = resourceData.activeColorTexture;
            TextureHandle blurA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, blurDesc, "_TiltShiftBlurA", false, FilterMode.Bilinear);
            TextureHandle blurB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, blurDesc, "_TiltShiftBlurB", false, FilterMode.Bilinear);
            TextureHandle result = UniversalRenderer.CreateRenderGraphTexture(renderGraph, fullDesc, "_TiltShiftResult", false, FilterMode.Bilinear);

            // Blur separable con ping-pong: H(source) -> A, V(A) -> B, H(B) -> A, ...
            TextureHandle blurInput = source;
            int iterations = Mathf.Max(1, volume.iterations.value);
            for (int i = 0; i < iterations; i++)
            {
                AddBlitPass(renderGraph, "TiltShift Blur H", blurInput, blurA, PassBlurH);
                AddBlitPass(renderGraph, "TiltShift Blur V", blurA, blurB, PassBlurV);
                blurInput = blurB;
            }

            // Composición: mezcla nítido/desenfocado según la máscara vertical
            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("TiltShift Composite", out var passData, profilingSampler))
            {
                passData.source = source;
                passData.blurred = blurB;
                passData.material = material;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(blurB, AccessFlags.Read);
                builder.SetRenderAttachment(result, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (CompositePassData data, RasterGraphContext context) =>
                {
                    RTHandle blurred = data.blurred;
                    data.material.SetTexture(BlurTexId, blurred);
                    Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, PassComposite);
                });
            }

            // El resultado pasa a ser el color de cámara para el resto del pipeline (post-procesado URP, UI...)
            resourceData.cameraColor = result;
        }

        private void AddBlitPass(RenderGraph renderGraph, string name, TextureHandle source, TextureHandle destination, int shaderPass)
        {
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(name, out var passData, profilingSampler))
            {
                passData.source = source;
                passData.material = material;
                passData.shaderPass = shaderPass;

                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (BlitPassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, data.shaderPass);
                });
            }
        }
    }
}
