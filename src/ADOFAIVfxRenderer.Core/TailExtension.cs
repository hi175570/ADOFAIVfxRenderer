namespace ADOFAIVfxRenderer.Core;

public readonly record struct TailExtension(decimal Seconds)
{
    public static TailExtension Zero => new(0m);

    public static TailExtension FromSeconds(decimal seconds)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "尾部延长不能为负数。");
        }

        return new TailExtension(seconds);
    }

    public static TailExtension FromFrames(long frames, int referenceFramesPerSecond)
    {
        if (frames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "尾部帧数不能为负数。");
        }

        ValidateFrameRate(referenceFramesPerSecond);
        return new TailExtension(frames / (decimal)referenceFramesPerSecond);
    }

    public long GetFrameCount(int framesPerSecond)
    {
        ValidateFrameRate(framesPerSecond);
        return checked((long)decimal.Ceiling(Seconds * framesPerSecond));
    }

    private static void ValidateFrameRate(int framesPerSecond)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "帧率必须大于零。");
        }
    }
}
