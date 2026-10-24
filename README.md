# ADOFAI VFX Renderer

ADOFAI VFX Renderer is a Windows offline rendering tool for producing Video, Video Pro, Video VFX, and Video VFX Pro chart packages without manually playing or recording the chart in real time.

The renderer uses its own Unity 6000.3.10f1 player and never launches `A Dance of Fire and Ice.exe`. An existing game directory is selected only for build compatibility checks and permitted runtime dependencies such as FFmpeg and, in later adapters, original managed filter implementations. The project does not redistribute game code or assets.

## Current state

The current implementation milestone includes:

- game-directory discovery and verified-build capability probing;
- `.adofai` format-version inspection;
- the four video-background ownership profiles;
- 144p, 360p, 720p, 1080p, and 2160p presets;
- 15, 30, 60, 90, 120, and 144 FPS presets;
- synchronized tail-duration seconds and frame counts;
- exact multi-FPS timeline-union planning;
- shared highest-resolution render-pass planning;
- a dark WPF configuration interface with a large live preview, virtualized task queue, progress, frame counters, FPS and seconds-per-frame;
- an independently built Unity worker process with direct RenderTexture capture and raw-frame FFmpeg encoding;
- offline `AddDecoration`, `MoveDecorations`, and `MoveCamera` prototype support;
- selectable render content (background/foreground decorations, track, planets, and effects) with UI always excluded;
- quality-derived and custom kbps bitrate support;
- dependency-free core regression tests.

The worker is intentionally not described as pixel-accurate yet. The current clean-room v13-v19 event adapter is incomplete: advanced easing, repeats, masks, particles, objects, track/planet rendering, filters, and exact legacy camera semantics still need verified adapters. Video VFX is the only enabled end-to-end test profile.

## Build and test

```powershell
pwsh -File .\scripts\Build-UnityWorker.ps1
dotnet build .\ADOFAIVfxRenderer.slnx
dotnet run --project .\tests\ADOFAIVfxRenderer.Core.Tests\ADOFAIVfxRenderer.Core.Tests.csproj
```

## Compatibility policy

A chart is renderable only when the selected game installation has a verified adapter. Unknown binary builds are detected but rejected until their semantics are validated. The independent worker must never fall back to starting, copying, renaming, or hard-linking the game executable.

## License

License selection is pending before the first public release.
