using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Kolyanotranskribator;

public partial class MainWindow : Window
{
    private readonly TranscriptionService service;

    public MainWindow()
    {
        InitializeComponent();
        service = new TranscriptionService(AppContext.BaseDirectory);
        try { service.EnsureDirectories(); }
        catch (Exception ex) { StatusText.Text = $"Не удалось создать рабочие папки: {ex.Message}"; ProcessButton.IsEnabled = false; }
    }

    private void OpenIncomingClick(object sender, RoutedEventArgs e) => OpenFolder(service.Incoming);
    private void OpenOutgoingClick(object sender, RoutedEventArgs e) => OpenFolder(service.Outgoing);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async void ProcessClick(object sender, RoutedEventArgs e)
    {
        ProcessButton.IsEnabled = false;
        ProcessingIndicator.Visibility = Visibility.Visible;
        ScannerSweep.Visibility = Visibility.Visible;
        ResultsList.Items.Clear();
        try
        {
            var files = service.GetIncomingFiles();
            if (files.Length == 0) { StatusText.Text = "Во Входящих пока нет поддерживаемых файлов."; return; }
            int succeeded = 0, failed = 0;
            foreach (var file in files)
            {
                StatusText.Text = $"Обрабатывается: {Path.GetFileName(file)}";
                var progress = new Progress<string>(message => StatusText.Text = $"{Path.GetFileName(file)} · {message}");
                var result = await service.ProcessAsync(file, progress);
                ResultsList.Items.Add(result.Success
                    ? $"Готово: {Path.GetFileName(file)} → {Path.GetFileName(result.OutputPath)}"
                    : $"Ошибка: {Path.GetFileName(file)} — {result.Message}");
                if (result.Success) succeeded++; else failed++;
            }
            StatusText.Text = $"Готово: {succeeded} успешно, {failed} с ошибкой.";
        }
        catch (Exception ex) { StatusText.Text = $"Обработка не запущена: {ex.Message}"; }
        finally
        {
            ProcessButton.IsEnabled = true;
            ProcessingIndicator.Visibility = Visibility.Collapsed;
            ScannerSweep.Visibility = Visibility.Collapsed;
        }
    }
}
