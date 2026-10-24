using System;
using System.Collections.Generic;
using System.Linq;

namespace ADOFAIVfxTimeline
{
public sealed class TimelineActionData
{
    public int Order;
    public string EventType = "";
    public int Floor;
    public double AngleOffset;
    public string SpeedType = "";
    public double BeatsPerMinute;
    public double BpmMultiplier = 1d;
    public double Duration;
    public string Planets = "";
}

public sealed class AdoTimelineResult
{
    private readonly double _pitch;
    private readonly double[] _floorStartAngles;
    private readonly SpeedSegment[] _segments;

    internal AdoTimelineResult(
        double pitch,
        double[] floorTimesSeconds,
        double[] floorBpms,
        double[] floorStartAngles,
        double[] rotationAngles,
        SpeedSegment[] segments)
    {
        _pitch = pitch;
        FloorTimesSeconds = floorTimesSeconds;
        FloorBpms = floorBpms;
        _floorStartAngles = floorStartAngles;
        RotationAngles = rotationAngles;
        _segments = segments;
    }

    public double[] FloorTimesSeconds { get; }
    public double[] FloorBpms { get; }
    public double[] RotationAngles { get; }
    public double DurationSeconds => FloorTimesSeconds.Length == 0 ? 0d : FloorTimesSeconds[^1];

    public double TimeAtFloorAngleOffset(int floor, double angleOffset)
    {
        if (FloorTimesSeconds.Length == 0) return 0d;
        floor = Math.Max(0, Math.Min(floor, FloorTimesSeconds.Length - 1));
        if (Math.Abs(angleOffset) < 1e-12) return FloorTimesSeconds[floor];

        var originAngle = _floorStartAngles[Math.Min(floor, _floorStartAngles.Length - 1)];
        var targetAngle = originAngle + angleOffset;
        return IntegrateSeconds(targetAngle);
    }

    public double BpmAtFloorAngleOffset(int floor, double angleOffset)
    {
        if (FloorTimesSeconds.Length == 0) return 100d;
        floor = Math.Max(0, Math.Min(floor, FloorTimesSeconds.Length - 1));
        var originAngle = _floorStartAngles[Math.Min(floor, _floorStartAngles.Length - 1)];
        return BpmAt(originAngle + angleOffset);
    }

    private double IntegrateSeconds(double targetAngle)
    {
        if (targetAngle <= 0d)
        {
            return targetAngle / 180d * 60d / (Math.Max(0.000001d, BpmAt(0d)) * _pitch);
        }

        var seconds = 0d;
        foreach (var segment in _segments)
        {
            if (targetAngle <= segment.StartAngle) break;
            var end = Math.Min(targetAngle, segment.EndAngle);
            if (end > segment.StartAngle)
            {
                seconds += (end - segment.StartAngle) / 180d * 60d /
                           (Math.Max(0.000001d, segment.Bpm) * _pitch);
            }
            if (targetAngle <= segment.EndAngle) break;
        }
        return seconds;
    }

    private double BpmAt(double angle)
    {
        if (_segments.Length == 0) return 100d;
        foreach (var segment in _segments)
        {
            if (angle < segment.EndAngle || ReferenceEquals(segment, _segments[^1])) return segment.Bpm;
        }
        return _segments[^1].Bpm;
    }
}

internal sealed class SpeedSegment
{
    public SpeedSegment(double startAngle, double endAngle, double bpm)
    {
        StartAngle = startAngle;
        EndAngle = endAngle;
        Bpm = bpm;
    }
    public double StartAngle { get; }
    public double EndAngle { get; }
    public double Bpm { get; }
}

public static class AdoTimelineCalculator
{
    public static AdoTimelineResult Calculate(
        IReadOnlyList<double> sourceAngles,
        IReadOnlyList<TimelineActionData> actions,
        double initialBpm,
        double pitch = 1d)
    {
        if (sourceAngles == null || sourceAngles.Count == 0)
            throw new ArgumentException("A chart must contain at least one angle.", nameof(sourceAngles));
        if (initialBpm <= 0d) throw new ArgumentOutOfRangeException(nameof(initialBpm));
        if (pitch <= 0d) throw new ArgumentOutOfRangeException(nameof(pitch));

        var angles = sourceAngles.Select(static angle => angle <= 0d ? Math.Round(angle + 360d, 8) : Math.Round(angle, 8)).ToArray();
        var rotations = BuildRotationAngles(angles, actions);
        var cumulativeAngles = new double[rotations.Length + 1];
        for (var index = 0; index < rotations.Length; index++)
        {
            cumulativeAngles[index + 1] = cumulativeAngles[index] +
                                          (Math.Abs(rotations[index] - 999d) < 0.000001d ? 0d : rotations[index]);
        }

        var segments = BuildSpeedSegments(actions, cumulativeAngles, rotations.Length, initialBpm);
        var result = new AdoTimelineResult(
            pitch,
            new double[rotations.Length + 1],
            new double[rotations.Length + 1],
            cumulativeAngles,
            rotations,
            segments);

        for (var floor = 0; floor <= rotations.Length; floor++)
        {
            result.FloorTimesSeconds[floor] = IntegrateTo(cumulativeAngles[floor], segments, pitch);
            result.FloorBpms[floor] = FindBpm(cumulativeAngles[floor], segments, initialBpm);
        }
        return result;
    }

    private static double[] BuildRotationAngles(double[] angles, IReadOnlyList<TimelineActionData> actions)
    {
        var compact = new List<double> { 0d };
        for (var index = 1; index < angles.Length; index++)
        {
            if (Math.Abs(angles[index - 1] - 999d) < 0.000001d) continue;
            double absoluteAngle1;
            double absoluteAngle2;
            if (Math.Abs(angles[index] - 999d) < 0.000001d && index + 1 < angles.Length)
            {
                absoluteAngle1 = angles[index - 1];
                absoluteAngle2 = angles[index + 1];
            }
            else
            {
                absoluteAngle1 = angles[index - 1] + 180d;
                absoluteAngle2 = Math.Abs(angles[index] - 999d) < 0.000001d ? angles[index - 1] : angles[index];
            }
            var rotation = PositiveModulo(absoluteAngle1 - absoluteAngle2, 360d);
            if (rotation < 0.001d || rotation > 359.999d) rotation = 360d;
            compact.Add(rotation);
        }

        foreach (var index in Enumerable.Range(0, angles.Length).Where(index => Math.Abs(angles[index] - 999d) < 0.000001d))
            compact.Insert(Math.Min(index, compact.Count), 999d);
        while (compact.Count < angles.Length) compact.Add(999d);
        if (compact.Count > angles.Length) compact.RemoveRange(angles.Length, compact.Count - angles.Length);

        var rotations = compact.ToArray();
        var twirls = actions.Where(static action => action.EventType == "Twirl").Select(static action => action.Floor).ToArray();
        for (var pair = 0; pair < twirls.Length; pair += 2)
        {
            var start = Math.Max(0, twirls[pair]);
            var end = pair + 1 < twirls.Length ? Math.Min(rotations.Length, twirls[pair + 1]) : rotations.Length;
            for (var floor = start; floor < end; floor++)
                if (rotations[floor] < 360d) rotations[floor] = 360d - rotations[floor];
        }

        var planetChanges = new SortedDictionary<int, int> { [0] = 2, [rotations.Length] = 2 };
        foreach (var action in actions.Where(static action => action.EventType == "MultiPlanet"))
            planetChanges[Math.Max(0, Math.Min(rotations.Length, action.Floor))] = action.Planets == "ThreePlanets" ? 3 : 2;
        var threePlanetStart = -1;
        foreach (var change in planetChanges)
        {
            if (change.Value == 3 && threePlanetStart < 0)
            {
                threePlanetStart = change.Key;
            }
            else if (change.Value == 2 && threePlanetStart >= 0)
            {
                for (var floor = threePlanetStart; floor < change.Key && floor < rotations.Length; floor++)
                {
                    if (floor > 0 && Math.Abs(rotations[floor] - 999d) > 0.000001d &&
                        Math.Abs(rotations[floor - 1] - 999d) > 0.000001d)
                    {
                        rotations[floor] -= 60d;
                        if (rotations[floor] <= 0d) rotations[floor] += 360d;
                    }
                }
                threePlanetStart = -1;
            }
        }

        foreach (var action in actions)
        {
            if (action.Floor < 0 || action.Floor >= rotations.Length) continue;
            if (action.EventType == "Hold") rotations[action.Floor] += action.Duration * 360d;
            else if (action.EventType == "Pause") rotations[action.Floor] += action.Duration * 180d;
            else if (action.EventType == "FreeRoam") rotations[action.Floor] += (action.Duration - 1d) * 180d;
        }
        return rotations;
    }

    private static SpeedSegment[] BuildSpeedSegments(
        IReadOnlyList<TimelineActionData> actions,
        double[] cumulativeAngles,
        int floorCount,
        double initialBpm)
    {
        var totalAngle = cumulativeAngles[^1];
        var speedEvents = actions
            .Where(action => action.EventType == "SetSpeed" && action.Floor >= 0 && action.Floor < floorCount)
            .Select(action => new
            {
                Angle = Math.Max(0d, Math.Min(totalAngle, cumulativeAngles[action.Floor] + action.AngleOffset)),
                Action = action,
            })
            .OrderBy(item => item.Angle)
            .ThenBy(item => item.Action.Order)
            .ToArray();

        var segments = new List<SpeedSegment>();
        var position = 0d;
        var bpm = initialBpm;
        foreach (var speedEvent in speedEvents)
        {
            if (speedEvent.Angle > position)
                segments.Add(new SpeedSegment(position, speedEvent.Angle, bpm));
            bpm = speedEvent.Action.SpeedType == "Bpm"
                ? speedEvent.Action.BeatsPerMinute
                : bpm * speedEvent.Action.BpmMultiplier;
            if (bpm <= 0d) throw new InvalidOperationException("SetSpeed produced a non-positive BPM.");
            position = speedEvent.Angle;
        }
        if (totalAngle > position) segments.Add(new SpeedSegment(position, totalAngle, bpm));
        if (segments.Count == 0) segments.Add(new SpeedSegment(0d, Math.Max(0d, totalAngle), bpm));
        return segments.ToArray();
    }

    private static double IntegrateTo(double angle, IReadOnlyList<SpeedSegment> segments, double pitch)
    {
        var seconds = 0d;
        foreach (var segment in segments)
        {
            var end = Math.Min(angle, segment.EndAngle);
            if (end > segment.StartAngle)
                seconds += (end - segment.StartAngle) / 180d * 60d / (segment.Bpm * pitch);
            if (angle <= segment.EndAngle) break;
        }
        return seconds;
    }

    private static double FindBpm(double angle, IReadOnlyList<SpeedSegment> segments, double fallback)
    {
        foreach (var segment in segments)
            if (angle < segment.EndAngle) return segment.Bpm;
        return segments.Count == 0 ? fallback : segments[^1].Bpm;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        var result = value % modulus;
        return result < 0d ? result + modulus : result;
    }
}
}
