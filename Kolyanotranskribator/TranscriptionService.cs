using System.Diagnostics;
using System.IO;
using System.Text;

namespace Kolyanotranskribator;

public sealed record TranscriptionResult(bool Success, string Message, string? OutputPath = null);

public sealed class TranscriptionService
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".m4a", ".ogg", ".flac", ".mp4", ".mov", ".mkv", ".webm" };
    private readonly string root;
    public string Incoming => Path.Combine(root, "Входящие");
    public string Outgoing => Path.Combine(root, "Исходящие");

    public TranscriptionService(string root) => this.root = root;

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Incoming);
        Directory.CreateDirectory(Outgoing);
    }

    public string[] GetIncomingFiles() => Directory.GetFiles(Incoming)
        .Where(path => Supported.Contains(Path.GetExtension(path)))
        .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public async Task<TranscriptionResult> ProcessAsync(string source, IProgress<string>? progress = null)
    {
        string? job = null;
        try
        {
            var ffmpeg = Path.Combine(root, "tools", "ffmpeg.exe");
            var whisper = Path.Combine(root, "tools", "whisper-cli.exe");
            var bundledModel = Path.Combine(root, "tools", "ggml-small.bin");
            var bundledSegmentation = Path.Combine(root, "tools", "diarization", "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
            var bundledEmbedding = Path.Combine(root, "tools", "diarization", "embedding.onnx");
            foreach (var path in new[] { ffmpeg, whisper, bundledModel, bundledSegmentation, bundledEmbedding })
                if (!File.Exists(path)) return new(false, $"Не найден компонент {Path.GetFileName(path)}.");

            // The upstream Windows CLI cannot read Cyrillic paths reliably. Stage its inputs in an ASCII path.
            var publicRoot = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
            var workRoot = Path.Combine(publicRoot, "Kolyanotranskribator");
            if (workRoot.Any(ch => ch > 127)) return new(false, "Не найден рабочий путь без русских букв.");
            Directory.CreateDirectory(workRoot);
            var model = Path.Combine(workRoot, "ggml-small.bin");
            if (!File.Exists(model) || new FileInfo(model).Length != new FileInfo(bundledModel).Length)
            {
                progress?.Report("подготовка модели");
                await Task.Run(() => File.Copy(bundledModel, model, true));
            }
            foreach (var (sourceModel, fileName) in new[]
            {
                (bundledSegmentation, "speaker-segmentation.onnx"),
                (bundledEmbedding, "speaker-embedding.onnx")
            })
            {
                var destination = Path.Combine(workRoot, fileName);
                if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(sourceModel).Length)
                    await Task.Run(() => File.Copy(sourceModel, destination, true));
            }
            job = Path.Combine(workRoot, "job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            var wave = Path.Combine(job, "audio.wav");
            var transcriptBase = Path.Combine(job, "transcript");

            progress?.Report("подготовка звука");
            var ffmpegResult = await RunAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", source,
                "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wave);
            if (ffmpegResult.ExitCode != 0 || !File.Exists(wave) || new FileInfo(wave).Length <= 44)
                return new(false, "Не удалось прочитать звук. Проверьте файл и звуковую дорожку.");

            progress?.Report("распознавание речи");
            var whisperResult = await RunAsync(whisper, "-m", model, "-f", wave, "-l", "auto",
                "-osrt", "-of", transcriptBase, "-nt", "-ml", "100", "-sow");
            var generated = transcriptBase + ".srt";
            if (whisperResult.ExitCode != 0 || !File.Exists(generated))
                return new(false, "Распознавание не удалось. Проверьте модель и запись.");
            progress?.Report("разделение собеседников");
            var diarization = await Task.Run(() => SpeakerDiarizer.Detect(wave, workRoot));
            var text = TranscriptFormatter.Format(await File.ReadAllTextAsync(generated, Encoding.UTF8), diarization);
            if (text.Length == 0) return new(false, "В записи не удалось распознать речь.");

            var baseName = Path.GetFileNameWithoutExtension(source);
            var extension = Path.GetExtension(source);
            for (int index = 1; index < 10000; index++)
            {
                var name = index == 1 ? baseName : $"{baseName} ({index})";
                var mediaTarget = Path.Combine(Outgoing, name + extension);
                var textTarget = Path.Combine(Outgoing, name + ".txt");
                if (File.Exists(mediaTarget) || File.Exists(textTarget)) continue;
                progress?.Report("сохранение результата");
                FileStream stream;
                try { stream = new FileStream(textTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (File.Exists(textTarget)) { continue; }
                try
                {
                    await using (stream)
                    await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        await writer.WriteAsync(text);
                }
                catch
                {
                    stream.Dispose();
                    File.Delete(textTarget);
                    throw;
                }
                try { File.Move(source, mediaTarget); }
                catch
                {
                    File.Delete(textTarget);
                    throw;
                }
                return new(true, "Готово", textTarget);
            }
            return new(false, "Не удалось подобрать свободное имя для результата.");
        }
        catch (IOException) { return new(false, "Не удалось прочитать, сохранить или переместить файл. Проверьте доступ к нему и свободное место."); }
        catch (UnauthorizedAccessException) { return new(false, "Нет доступа к файлу или рабочей папке."); }
        catch (Exception ex) { return new(false, $"Ошибка обработки: {ex.Message}"); }
        finally
        {
            if (job != null && Directory.Exists(job))
                try { Directory.Delete(job, true); } catch { /* Keep the original error available. */ }
        }
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить " + Path.GetFileName(file));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        return (process.ExitCode, await error);
    }
}
