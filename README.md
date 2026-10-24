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
- exact multi-FPS timeline-union planning and sequential multi-output execution;
- shared highest-resolution render-pass planning for a later frame-reuse optimization;
- a dark WPF configuration interface with a large live preview, virtualized task queue, progress, frame counters, FPS and seconds-per-frame;
- an independently built Unity worker process with direct RenderTexture capture and raw-frame FFmpeg encoding;
- offline `AddDecoration`, `MoveDecorations`, `MoveCamera`, `CustomBackground`, and beat/floor `RepeatEvents` support;
- a shared v13-v19 rhythm timeline for midspins, twirls, three-planet sections, holds, pauses, free-roam spans, BPM changes, and speed-event angle offsets;
- the full easing families used by the current target chart, per-property tween replacement, tiled images, sprite masks, depth ranges, and Difference/Overlay/SoftLight/LinearDodge blend modes;
- automatic non-destructive derived-chart export with `bgVideo`, a calculated signed `vidOffset`, and removal of only the visual elements actually baked into the selected profile;
- first-floor synchronization: the exporter compares the game's video trigger time with the actual first playable floor, so normal intros produce `0 ms` while unusual first angles or floor-zero speed events can correctly produce positive or negative offsets;
- selectable render content (background/foreground decorations, track, planets, and effects) with UI always excluded;
- quality-derived and custom kbps bitrate support;
- dependency-free core regression tests.

The worker is intentionally not described as pixel-accurate for every chart yet. Image decorations used by the current target chart have verified timeline, masking, blending, repeat, camera, and tween adapters. Text, particle, and object decorations remain live in the derived chart instead of being silently dropped; target-tag alpha masks, exact legacy camera edge cases, track/planet rendering, and filters still need verified offline adapters. Video VFX and depth-split Video VFX Pro are enabled end to end; Video and Video Pro remain disabled until their offline filter path is verified.

## Build and test

```powershell
pwsh -File .\scripts\Build-UnityWorker.ps1
pwsh -File .\scripts\Publish-TestPackage.ps1
dotnet build .\ADOFAIVfxRenderer.slnx
dotnet run --project .\tests\ADOFAIVfxRenderer.Core.Tests\ADOFAIVfxRenderer.Core.Tests.csproj
```

## Compatibility policy

A chart is renderable only when the selected game installation has a verified adapter. Unknown binary builds are detected but rejected until their semantics are validated. The independent worker must never fall back to starting, copying, renaming, or hard-linking the game executable.

## License

License selection is pending before the first public release.
