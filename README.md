# ADOFAI VFX Renderer

ADOFAI VFX Renderer is a Windows offline rendering tool for producing Video, Video Pro, Video VFX, and Video VFX Pro chart packages without manually playing or recording the chart in real time.

The renderer uses its own Unity 6000.3.10f1 player and never launches `A Dance of Fire and Ice.exe`. An existing game directory is selected only for build compatibility checks and permitted runtime dependencies such as FFmpeg and, in later adapters, original managed filter implementations. The project does not redistribute game code or assets.

## Current state

The current implementation milestone includes:

- game-directory discovery and verified-build capability probing;
- `.adofai` format-version inspection plus in-memory conversion through the selected game's own `LevelData.LoadLevel`/`Encode` path before rendering;
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
- the full easing families used by the current target chart, per-property tween replacement, player-follow camera motion, tiled images, empty-target alpha-mask behavior, masking depth ranges, and Difference/Overlay/SoftLight/LinearDodge blend modes;
- automatic non-destructive derived-chart export with `bgVideo`, a calculated signed `vidOffset`, and removal of only the visual elements actually baked into the selected profile;
- automatic preservation of any image decoration the worker could not load or bake, preventing a failed image from being removed from both the video and the derived chart;
- first-floor synchronization: the exporter compares the game's video trigger time with the actual first playable floor, so normal intros produce `0 ms` while unusual first angles or floor-zero speed events can correctly produce positive or negative offsets;
- a full-page Video VFX retention editor with whitelist/blacklist semantics, reference-order/depth sorting, transparent-image previews, and non-destructive integration with both worker baking and derived-chart export;
- paired decoration diagnostics that render ten identical timestamps with and without the selected decoration (twenty images total) and show every pair inside the app;
- a `滤镜与装饰` workspace with per-filter ownership: keep the group live in the chart, bake a supported offline approximation into the video, or explicitly ignore it; baked/ignored groups are disabled only in the generated chart;
- filter grouping by basic/advanced type, plane, target type, and target tag, so filters aimed at different objects are never silently merged into one choice;
- the installed game's original basic/advanced camera-filter components and parameter conversion, including signed values, legacy string-form `filterProperties`, Bloom, ScreenTile, ScreenScroll, and frame-history Hall of Mirrors in profiles that bake the complete scene;
- one stable `Video VFX` folder beside the source chart: each output profile owns one canonical MP4 and one canonical derived chart, and a successful rerender atomically replaces the previous file instead of producing timestamped backups;
- remembered game/chart paths, a dedicated settings tab, and guarded cleanup of only app-generated diagnostic caches;
- selectable automatic, CPU/WARP, hardware-GPU, and NVIDIA/RTX/NVENC backends with capability probing and no silent fallback;
- selectable background/foreground decoration content with UI always excluded;
- quality-derived and custom kbps bitrate support;
- a separate internal effect canvas: filter-bearing outputs render at a minimum 1080p reference canvas before Lanczos downscaling, so choosing a small delivery resolution does not multiply pixel-radius blur and erase the track;
- dependency-free core regression tests.

The worker is intentionally not described as pixel-accurate for every chart yet. Image decorations used by the current target chart have verified timeline, tiling, v19 `NO TAG` masking semantics, masking depth ranges, blending, repeat, player-follow camera, unwrapped full-turn camera rotation, and tween adapters. Text and object decorations that are not yet supported remain live in the derived chart instead of being silently dropped. Video, Video Pro, Video VFX, and depth-split Video VFX Pro are enabled end to end; difficult charts still require comparison against the installed game's output, especially when filters target individual tagged decorations.

## Build and test

```powershell
pwsh -File .\scripts\Build-UnityWorker.ps1
pwsh -File .\scripts\Publish-TestPackage.ps1
dotnet build .\ADOFAIVfxRenderer.slnx
dotnet run --project .\tests\ADOFAIVfxRenderer.Core.Tests\ADOFAIVfxRenderer.Core.Tests.csproj
```

## Compatibility policy

A chart is renderable only when the selected game installation has a verified adapter. The current supported baseline is the detected 3.x / Unity 6000.3.10f1 installation. Older 2.9.x Unity builds are not assumed binary-compatible and are rejected until a matching worker and real installation are validated. Unknown binary builds are detected but rejected rather than silently using current-version assumptions. The independent worker must never fall back to starting, copying, renaming, or hard-linking the game executable.

## License

License selection is pending before the first public release.
