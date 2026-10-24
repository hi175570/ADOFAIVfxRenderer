# ADOFAI VFX Renderer

ADOFAI VFX Renderer is a Windows offline rendering tool for producing Video, Video Pro, Video VFX, and Video VFX Pro chart packages without manually playing or recording the chart in real time.

The renderer is designed to use an existing A Dance of Fire and Ice installation at runtime. It does not redistribute the game's managed assemblies, shaders, or assets. Users select the directory containing `A Dance of Fire and Ice.exe`; the application resolves the Unity data and `Managed` paths automatically.

## Current state

The first implementation milestone includes:

- game-directory discovery and verified-build capability probing;
- `.adofai` format-version inspection;
- the four video-background ownership profiles;
- 144p, 360p, 720p, 1080p, and 2160p presets;
- 15, 30, 60, 90, 120, and 144 FPS presets;
- synchronized tail-duration seconds and frame counts;
- exact multi-FPS timeline-union planning;
- shared highest-resolution render-pass planning;
- a runnable WPF configuration interface;
- dependency-free core regression tests.

The Unity render worker and video encoder are not connected yet. The UI does not present a fake Render button while that path is unavailable.

## Build and test

```powershell
dotnet build .\src\ADOFAIVfxRenderer.App\ADOFAIVfxRenderer.App.csproj
dotnet run --project .\tests\ADOFAIVfxRenderer.Core.Tests\ADOFAIVfxRenderer.Core.Tests.csproj
```

## Compatibility policy

A chart is renderable only when the selected game installation contains the required original event, camera, track, decoration, and filter components and has a verified renderer adapter. Unknown binary builds are detected but rejected until their behavior is validated.

## License

License selection is pending before the first public release.
