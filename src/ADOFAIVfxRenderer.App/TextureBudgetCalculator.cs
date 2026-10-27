using System.IO;
using System.Windows.Media.Imaging;
using ADOFAIVfxRenderer.Core;

namespace ADOFAIVfxRenderer.App;

internal sealed record TextureBudgetPlan(
    int PlaceholderMaximumSide,
    int TextureCount,
    long FullResolutionBytes,
    long BudgetBytes);

internal static class TextureBudgetCalculator
{
    // Placeholders are never written to the video. They only let the original game
    // instantiate a decoration without decoding its source image up front.
    private const int PlaceholderMaximumSide = 16;

    public static TextureBudgetPlan Calculate(
        string chartPath,
        int budgetMb)
    {
        if (budgetMb <= 0)
            return new TextureBudgetPlan(0, 0, 0, 0);
        var textures = ChartTextureCatalogReader.ReadReferencedFiles(chartPath)
            .Select(ReadDimensions)
            .ToArray();
        var budgetBytes = budgetMb * 1024L * 1024L;
        if (textures.Length == 0)
            return new TextureBudgetPlan(PlaceholderMaximumSide, 0, 0, budgetBytes);

        return new TextureBudgetPlan(
            PlaceholderMaximumSide,
            textures.Length,
            EstimateFullResolutionBytes(textures),
            budgetBytes);
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (Math.Max(1, frame.PixelWidth), Math.Max(1, frame.PixelHeight));
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or FileFormatException)
        {
            // Unknown formats are budgeted as square textures at the selected cap.
            return (int.MaxValue, int.MaxValue);
        }
    }

    private static long EstimateFullResolutionBytes(IEnumerable<(int Width, int Height)> textures)
    {
        long total = 0;
        foreach (var texture in textures)
        {
            try
            {
                total = checked(total + (long)texture.Width * texture.Height * 4L);
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }
        return total;
    }
}
