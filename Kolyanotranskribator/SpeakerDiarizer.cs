using SherpaOnnx;
using System.IO;

namespace Kolyanotranskribator;

public sealed record SpeakerInterval(double Start, double End, int Speaker);
public sealed record DiarizationResult(SpeakerInterval[] Intervals, TimeSpan Duration);

public static class SpeakerDiarizer
{
    public static DiarizationResult Detect(string wavePath, string modelDirectory)
    {
        var segmentation = Path.Combine(modelDirectory, "speaker-segmentation.onnx");
        var embedding = Path.Combine(modelDirectory, "speaker-embedding.onnx");
        if (!File.Exists(segmentation) || !File.Exists(embedding))
            throw new FileNotFoundException("Не найдены модели разделения собеседников.");

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation;
        config.Segmentation.Pyannote.WindowShiftRatio = 0.1f;
        config.Embedding.Model = embedding;
        config.Clustering.Threshold = 0.9f;
        using var diarizer = new OfflineSpeakerDiarization(config);
        var (samples, sampleRate) = ReadWave(wavePath);
        if (sampleRate != diarizer.SampleRate)
            throw new InvalidDataException("Неверная частота звука для разделения собеседников.");
        var labels = new Dictionary<int, int>();
        var intervals = diarizer.Process(samples)
            .OrderBy(segment => segment.Start)
            .Select(segment =>
            {
                if (!labels.TryGetValue(segment.Speaker, out var label))
                    labels[segment.Speaker] = label = labels.Count + 1;
                return new SpeakerInterval(segment.Start, segment.End, label);
            }).ToArray();
        return new(intervals, TimeSpan.FromSeconds((double)samples.Length / sampleRate));
    }

    private static (float[] Samples, int SampleRate) ReadWave(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF" || reader.ReadUInt32() < 36 ||
            new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("Неверный формат подготовленного звука.");
        int sampleRate = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var end = reader.BaseStream.Position + size;
            if (end > reader.BaseStream.Length) throw new InvalidDataException("Неполный аудиофайл.");
            if (chunk == "fmt ")
            {
                if (reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1)
                    throw new InvalidDataException("Ожидался одноканальный PCM звук.");
                sampleRate = reader.ReadInt32();
                reader.BaseStream.Position += 6;
                if (reader.ReadUInt16() != 16)
                    throw new InvalidDataException("Ожидался 16-битный звук.");
            }
            else if (chunk == "data" && sampleRate != 0)
            {
                var count = checked((int)size / 2);
                var samples = new float[count];
                for (var index = 0; index < count; index++) samples[index] = reader.ReadInt16() / 32768f;
                return (samples, sampleRate);
            }
            reader.BaseStream.Position = end + (size % 2);
        }
        throw new InvalidDataException("В подготовленном файле нет звука.");
    }
}
