namespace ADOFAIVfxRenderer.Core;

public sealed record RenderOutputLayout(
    string OutputDirectory,
    string VideoPath,
    string PreviewDirectory,
    string JobPath,
    string StatusPath,
    string WorkerLogPath,
    string DerivedChartPath)
{
    public const string FolderName = "Video VFX";

    public static RenderOutputLayout Create(string sourceChartPath, OutputProfile profile)
    {
        sourceChartPath = Path.GetFullPath(sourceChartPath);
        var chartDirectory = Path.GetDirectoryName(sourceChartPath)
                             ?? throw new ArgumentException("谱面路径缺少父目录。", nameof(sourceChartPath));
        var outputDirectory = Path.Combine(chartDirectory, FolderName);
        var profileName = OutputProfiles.Get(profile).DisplayName;
        var fileStem = MakeFileNameSafe(profileName);
        var chartName = Path.GetFileNameWithoutExtension(sourceChartPath);

        return new RenderOutputLayout(
            outputDirectory,
            Path.Combine(outputDirectory, fileStem + ".mp4"),
            Path.Combine(outputDirectory, ".preview", fileStem),
            Path.Combine(outputDirectory, $"render-job-{fileStem}.json"),
            Path.Combine(outputDirectory, $"render-status-{fileStem}.json"),
            Path.Combine(outputDirectory, $"worker-{fileStem}.log"),
            Path.Combine(chartDirectory, $"{chartName} - {fileStem}.adofai"));
    }

    private static string MakeFileNameSafe(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');
        return value.Trim();
    }
}
