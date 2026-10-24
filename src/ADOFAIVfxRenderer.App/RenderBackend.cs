namespace ADOFAIVfxRenderer.App;

internal enum RenderBackend
{
    Automatic,
    CpuCompatibility,
    HardwareGpu,
    NvidiaRtx,
}

internal sealed record ResolvedRenderBackend(
    RenderBackend Backend,
    string VideoEncoder,
    string EncoderPreset,
    string UnityArguments,
    string Description);
