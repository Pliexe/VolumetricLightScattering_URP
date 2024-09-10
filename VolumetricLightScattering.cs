using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[System.Serializable]
public class VolumetricLightScatteringSettings
{
    [Header("Properties")]
    [Range(0.1f, 1f)]
    public float resolutionScale = 0.5f;

    [Range(0.0f, 1.0f)]
    public float intensity = 1.0f;

    [Range(0.0f, 1.0f)]
    public float blurWidth = 0.85f;
}

public class VolumetricLightScattering : ScriptableRendererFeature
{
    class LightScatteringPass : ScriptableRenderPass
    {
        private RTHandle cameraColorTargetIdent;
        private FilteringSettings filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
        private readonly List<ShaderTagId> shaderTagIdList = new List<ShaderTagId>();
        private RTHandle occluders;
        private readonly float resolutionScale;
        private readonly float intensity;
        private readonly float blurWidth;
        private readonly Material occludersMaterial;
        private readonly Material radialBlurMaterial;

        public LightScatteringPass(VolumetricLightScatteringSettings settings)
        {
            resolutionScale = settings.resolutionScale;
            intensity = settings.intensity;
            blurWidth = settings.blurWidth;

            occludersMaterial = new Material(Shader.Find("Hidden/RW/UnlitColor"));
            radialBlurMaterial = new Material(Shader.Find("Hidden/RW/RadialBlur"));

            shaderTagIdList.Add(new ShaderTagId("UniversalForward"));
            shaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
            shaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor cameraTextureDescriptor = renderingData.cameraData.cameraTargetDescriptor;

            cameraTextureDescriptor.depthBufferBits = 0;
            
            cameraTextureDescriptor.width = Mathf.RoundToInt(cameraTextureDescriptor.width * resolutionScale);
            cameraTextureDescriptor.height = Mathf.RoundToInt(cameraTextureDescriptor.height * resolutionScale);

            RenderingUtils.ReAllocateIfNeeded(ref occluders, cameraTextureDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_OccludersMap");
            
            cameraColorTargetIdent = renderingData.cameraData.renderer.cameraColorTargetHandle;

            ConfigureTarget(occluders);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!occludersMaterial || !radialBlurMaterial)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, new ProfilingSampler("VolumetricLightScattering")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                Camera camera = renderingData.cameraData.camera;
                context.DrawSkybox(camera);

                DrawingSettings drawSettings = CreateDrawingSettings(shaderTagIdList, ref renderingData, SortingCriteria.CommonOpaque);
                drawSettings.overrideMaterial = occludersMaterial;

                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);

                Vector3 sunDirectionWorldSpace = RenderSettings.sun.transform.forward;
                Vector3 cameraPositionWorldSpace = camera.transform.position;
                Vector3 sunPositionWorldSpace = cameraPositionWorldSpace + sunDirectionWorldSpace;
                Vector3 sunPositionViewportSpace = camera.WorldToViewportPoint(sunPositionWorldSpace);

                float modifiedIntensity;
                float sunDistanceFromCenter = Vector2.Distance(new Vector2(sunPositionViewportSpace.x, sunPositionViewportSpace.y), new Vector2(0.5f, 0.5f));
                if (sunDistanceFromCenter > 1)
                {
                    modifiedIntensity = intensity * Mathf.Clamp01(-sunPositionViewportSpace.z * 2);
                }
                else {
                    if (sunPositionViewportSpace.z < 0)
                    {
                        modifiedIntensity = intensity;
                    }
                    else {
                        modifiedIntensity = intensity * Mathf.Clamp01(-sunPositionViewportSpace.z);
                    }
                }
                
                radialBlurMaterial.SetVector("_Center", new Vector4(sunPositionViewportSpace.x, sunPositionViewportSpace.y, 0, 0));
                radialBlurMaterial.SetFloat("_Intensity", modifiedIntensity);
                radialBlurMaterial.SetFloat("_BlurWidth", blurWidth);

                cmd.Blit(occluders, cameraColorTargetIdent, radialBlurMaterial);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Dispose()
        {
            occluders?.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        m_ScriptablePass.Dispose();
    }

    public VolumetricLightScatteringSettings settings = new VolumetricLightScatteringSettings();

    private LightScatteringPass m_ScriptablePass;

    public override void Create()
    {
        m_ScriptablePass = new LightScatteringPass(settings);
        m_ScriptablePass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
    }
}
