using System.Text;
using System.Text.RegularExpressions;

namespace ADOFAIVfxRenderer.Core;

public sealed record ChartMetadata(string Path, int Version);

public static partial class ChartMetadataReader
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumHeaderCharacters = 32 * 1024 * 1024;

    public static ChartMetadata Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到 .adofai 谱面。", path);
        }

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize);
        var buffer = new char[BufferSize];
        var scanned = new StringBuilder(BufferSize * 2);

        while (scanned.Length < MaximumHeaderCharacters)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            scanned.Append(buffer, 0, read);
            var match = VersionRegex().Match(scanned.ToString());
            if (match.Success)
            {
                return new ChartMetadata(Path.GetFullPath(path), int.Parse(match.Groups[1].Value));
            }

            if (scanned.Length > BufferSize * 2)
            {
                scanned.Remove(0, scanned.Length - BufferSize);
            }
        }

        throw new InvalidDataException("谱面前部找不到 settings.version，无法判断编辑器格式版本。");
    }

    public static string? CheckCompatibility(ChartMetadata chart, GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(installation);

        if (!installation.CanRender)
        {
            return "所选游戏安装尚未通过渲染兼容性验证。";
        }

        if (installation.Adapter is null)
        {
            return "所选游戏版本没有已验证的适配器。";
        }

        return chart.Version > installation.Adapter.MaximumChartVersion
            ? $"谱面格式为 {chart.Version}，所选游戏最多支持 {installation.Adapter.MaximumChartVersion}。"
            : null;
    }

    [GeneratedRegex("\\\"version\\\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
