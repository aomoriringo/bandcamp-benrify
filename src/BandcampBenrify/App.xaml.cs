using System.Net;
using System.Windows;
using System.Windows.Threading;
using BandcampBenrify.Infrastructure;
using BandcampBenrify.Services;
using BandcampBenrify.ViewModels;

namespace BandcampBenrify;

public partial class App : Application
{
    private MainViewModel? _vm;
    private LocalApiServer? _api;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings);

        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;
        window.Show();

        if (settings.LocalApiPort > 0)
        {
            try
            {
                _api = new LocalApiServer(settings.LocalApiPort, _vm.EnqueueFromExtensionAsync);
                _api.Start();
            }
            catch (HttpListenerException ex)
            {
                _vm.StatusText = $"Chrome 拡張用のポート {settings.LocalApiPort} を開けませんでした ({ex.Message})。アプリ単体では問題なく使えます。";
                _api = null;
            }
        }

        // コマンドライン引数で URL を渡されたらキューに追加
        foreach (var arg in e.Args.Where(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            _ = _vm.AddUrlAsync(arg);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _api?.Dispose();
        _vm?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "error.log"), $"[{DateTimeOffset.Now:O}] {e.Exception}{Environment.NewLine}");
        }
        catch (System.IO.IOException)
        {
        }

        if (_vm is not null && MainWindow is { IsLoaded: true })
        {
            _vm.StatusText = "予期しないエラー: " + e.Exception.Message;
        }
        else
        {
            MessageBox.Show(e.Exception.ToString(), "Bandcamp Benrify", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
    }
}
