using System.IO;
using System.Windows.Media.Imaging;
using ADOFAIVfxRenderer.Core;

namespace ADOFAIVfxRenderer.App;

internal sealed record TextureBudgetPlan(
    int MaximumSide,
    int TextureCount,
    long EstimatedBytes,
    long BudgetBytes);

internal static class TextureBudgetCalculator
{
    public static TextureBudgetPlan Calculate(
        string chartPath,
        int budgetMb,
        int outputMaximumSide)
    {
        if (budgetMb <= 0)
            return new TextureBudgetPlan(0, 0, 0, 0);
        var textures = ChartTextureCatalogReader.ReadReferencedFiles(chartPath)
            .Select(ReadDimensions)
            .ToArray();
        var budgetBytes = budgetMb * 1024L * 1024L;
        if (textures.Length == 0)
            return new TextureBudgetPlan(outputMaximumSide, 0, 0, budgetBytes);

        var usableBudget = (long)(budgetBytes * 0.9d);
        var low = 1;
        var high = Math.Max(low, outputMaximumSide);
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (EstimateBytes(textures, middle) <= usableBudget) low = middle;
            else high = middle - 1;
        }
        var aligned = low >= 16 ? low / 16 * 16 : low;
        return new TextureBudgetPlan(
            aligned,
            textures.Length,
            EstimateBytes(textures, aligned),
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

    private static long EstimateBytes(IEnumerable<(int Width, int Height)> textures, int maximumSide)
    {
        long total = 0;
        foreach (var texture in textures)
        {
            var sourceMaximum = Math.Max(texture.Width, texture.Height);
            var scale = sourceMaximum <= maximumSide ? 1d : maximumSide / (double)sourceMaximum;
            var width = Math.Max(1, (int)Math.Round(texture.Width * scale));
            var height = Math.Max(1, (int)Math.Round(texture.Height * scale));
            total = checked(total + (long)width * height * 4L);
        }
        return total;
    }
}
