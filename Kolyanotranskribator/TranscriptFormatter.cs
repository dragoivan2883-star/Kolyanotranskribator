using System.Globalization;
using System.Text.RegularExpressions;

namespace Kolyanotranskribator;

public static class TranscriptFormatter
{
    private static readonly Regex Timing = new(
        @"^(?<start>\d+:\d\d:\d\d,\d\d\d) --> (?<end>\d+:\d\d:\d\d,\d\d\d)$",
        RegexOptions.Compiled);

    public static string Format(string srt, DiarizationResult diarization)
    {
        var output = new List<string>();
        foreach (var block in Regex.Split(srt.Trim(), @"\r?\n\r?\n"))
        {
            var lines = block.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            if (lines.Length < 3 || !int.TryParse(lines[0], out var _)) continue;
            var timing = Timing.Match(lines[1]);
            if (!timing.Success) continue;
            var start = ParseTime(timing.Groups["start"].Value);
            var end = ParseTime(timing.Groups["end"].Value);
            var phrase = string.Join(' ', lines.Skip(2));
            if (phrase.Length == 0) continue;
            if (start >= diarization.Duration)
            {
                if (output.Count > 0) output[^1] += " " + phrase;
                continue;
            }
            if (end > diarization.Duration) end = diarization.Duration;

            var bestSpeaker = diarization.Intervals
                .Select(speaker => new { speaker.Speaker, Overlap = Math.Max(0,
                    Math.Min(end.TotalSeconds, speaker.End) - Math.Max(start.TotalSeconds, speaker.Start)) })
                .OrderByDescending(candidate => candidate.Overlap).FirstOrDefault();
            var label = bestSpeaker is { Overlap: > 0 } ? $"Собеседник {bestSpeaker.Speaker}: " : "";
            output.Add($"[{Display(start)} — {Display(end)}] {label}{phrase}");
        }
        return string.Join(Environment.NewLine, output);
    }

    private static TimeSpan ParseTime(string value)
    {
        var parts = value.Split(':', ',');
        return new TimeSpan(0, int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    private static string Display(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
}
