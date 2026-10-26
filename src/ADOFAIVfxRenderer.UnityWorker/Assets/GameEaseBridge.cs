using System;
using System.Linq;
using System.Reflection;

namespace ADOFAIVfxWorker
{
internal static class GameEaseBridge
{
    private const float DefaultOvershootOrAmplitude = 1.70158f;
    private const float DefaultPeriod = 0f;
    private static readonly MethodInfo EvaluateMethod;
    private static readonly Type EaseType;
    private static readonly Type EaseFunctionType;

    static GameEaseBridge()
    {
        var dotween = AppDomain.CurrentDomain.GetAssemblies()
                          .FirstOrDefault(static assembly => assembly.GetName().Name == "DOTween")
                      ?? Assembly.Load("DOTween");
        EaseType = dotween.GetType("DG.Tweening.Ease", throwOnError: true)!;
        EaseFunctionType = dotween.GetType("DG.Tweening.EaseFunction", throwOnError: true)!;
        var manager = dotween.GetType("DG.Tweening.Core.Easing.EaseManager", throwOnError: true)!;
        EvaluateMethod = manager.GetMethod(
            "Evaluate",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { EaseType, EaseFunctionType, typeof(float), typeof(float), typeof(float), typeof(float) },
            null) ?? throw new MissingMethodException(manager.FullName, "Evaluate(Ease, EaseFunction, float, float, float, float)");
    }

    public static float Evaluate(float normalizedTime, string? easeName)
    {
        var name = string.IsNullOrWhiteSpace(easeName) ? "Linear" : easeName;
        var ease = Enum.Parse(EaseType, name, ignoreCase: false);
        return (float)(EvaluateMethod.Invoke(
            null,
            new object?[]
            {
                ease,
                null,
                Math.Clamp(normalizedTime, 0f, 1f),
                1f,
                DefaultOvershootOrAmplitude,
                DefaultPeriod,
            }) ?? throw new InvalidOperationException($"The installed game's DOTween evaluator returned no value for {name}."));
    }
}
}
