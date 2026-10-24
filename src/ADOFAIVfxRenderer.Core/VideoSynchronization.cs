using System.Globalization;
using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

public sealed record VideoSynchronization(
    decimal GameVideoStartSeconds,
    decimal FirstPlayableFloorSeconds,
    decimal RenderStartAfterFirstFloorSeconds,
    int VideoOffsetMilliseconds);

public static class VideoSynchronizationCalculator
{
    public static VideoSynchronization Calculate(JsonObject chart, decimal renderStartAfterFirstFloorSeconds = 0m)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var settings = chart["settings"] as JsonObject
                       ?? throw new InvalidDataException("谱面缺少 settings。");
        var bpm = ReadDecimal(settings["bpm"], 100m);
        if (bpm <= 0m) throw new InvalidDataException("settings.bpm 必须大于零。");

        var countdownTicks = settings["countdownTicks"]?.GetValue<int>() ?? 4;
        var separateCountdown = settings["separateCountdownTime"]?.GetValue<bool>() ?? false;
        var crotchet = 60m / bpm;
        var gameVideoStart = separateCountdown ? Math.Max(0, countdownTicks) * crotchet : 0m;
        var firstAngle = ReadFirstAngle(chart);
        var twirled = (chart["actions"] as JsonArray)?.OfType<JsonObject>()
            .Count(action => action["floor"]?.GetValue<int>() == 0 &&
                             action["eventType"]?.GetValue<string>() == "Twirl") % 2 == 1;
        var angleTravel = FirstFloorTravelDegrees(firstAngle, twirled);
        var firstTravelSeconds = IntegrateFirstFloorSpeed(chart["actions"] as JsonArray, angleTravel, bpm);
        var firstPlayableFloor = Math.Max(0, countdownTicks - 1) * crotchet + firstTravelSeconds;
        var offsetSeconds = gameVideoStart - (firstPlayableFloor + renderStartAfterFirstFloorSeconds);
        var offsetMilliseconds = checked((int)Math.Round(offsetSeconds * 1000m, MidpointRounding.AwayFromZero));
        return new VideoSynchronization(
            gameVideoStart,
            firstPlayableFloor,
            renderStartAfterFirstFloorSeconds,
            offsetMilliseconds);
    }

    private static decimal IntegrateFirstFloorSpeed(JsonArray? actions, decimal travelDegrees, decimal initialBpm)
    {
        var speedEvents = (actions ?? [])
            .OfType<JsonObject>()
            .Select((action, order) => new { Action = action, Order = order })
            .Where(item => item.Action["floor"]?.GetValue<int>() == 0 &&
                           item.Action["eventType"]?.GetValue<string>() == "SetSpeed")
            .Select(item => new
            {
                Angle = Math.Clamp(ReadDecimal(item.Action["angleOffset"], 0m), 0m, travelDegrees),
                item.Action,
                item.Order,
            })
            .OrderBy(item => item.Angle)
            .ThenBy(item => item.Order)
            .ToArray();

        var currentBpm = initialBpm;
        var position = 0m;
        var seconds = 0m;
        foreach (var speedEvent in speedEvents)
        {
            if (speedEvent.Angle > position)
                seconds += (speedEvent.Angle - position) / 180m * 60m / currentBpm;
            var speedType = speedEvent.Action["speedType"]?.GetValue<string>() ?? "Bpm";
            currentBpm = speedType == "Multiplier"
                ? currentBpm * ReadDecimal(speedEvent.Action["bpmMultiplier"], 1m)
                : ReadDecimal(speedEvent.Action["beatsPerMinute"], currentBpm);
            if (currentBpm <= 0m) throw new InvalidDataException("首格 SetSpeed 产生了非正 BPM。");
            position = speedEvent.Angle;
        }
        if (travelDegrees > position) seconds += (travelDegrees - position) / 180m * 60m / currentBpm;
        return seconds;
    }

    private static decimal FirstFloorTravelDegrees(decimal firstAngle, bool twirled)
    {
        if (firstAngle == 999m) return 0m;
        var clockwise = PositiveModulo(180m - firstAngle, 360m);
        return twirled ? PositiveModulo(-clockwise, 360m) : clockwise;
    }

    private static decimal ReadFirstAngle(JsonObject chart)
    {
        if (chart["angleData"] is JsonArray { Count: > 0 } angles)
            return ReadDecimal(angles[0], 0m);

        var path = chart["pathData"]?.GetValue<string>() ?? "";
        if (path.Length == 0) throw new InvalidDataException("谱面缺少 angleData/pathData。");
        return path[0] switch
        {
            'p' => 15m, 'J' => 30m, 'E' => 45m, 'T' => 60m, 'o' => 75m,
            'U' => 90m, 'q' => 105m, 'G' => 120m, 'Q' => 135m, 'H' => 150m,
            'W' => 165m, 'L' => 180m, 'x' => 195m, 'N' => 210m, 'Z' => 225m,
            'F' => 240m, 'V' => 255m, 'D' => 270m, 'Y' => 285m, 'B' => 300m,
            'C' => 315m, 'M' => 330m, 'A' => 345m, 'R' => 360m, '!' => 999m,
            _ => throw new InvalidDataException($"不支持的首格 pathData 字符：{path[0]}"),
        };
    }

    private static decimal ReadDecimal(JsonNode? node, decimal fallback)
    {
        if (node == null) return fallback;
        return decimal.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static decimal PositiveModulo(decimal value, decimal modulus)
    {
        var result = value % modulus;
        return result < 0m ? result + modulus : result;
    }
}
