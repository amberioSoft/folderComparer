using System.Windows;
using FolderDiff.Services;
using FolderDiff.ViewModels;
using FolderDiff.Views;

namespace FolderDiff;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _viewModel = new MainViewModel(new DialogService(), new SettingsService(), new ImageTransferService());

        if (e.Args.Length >= 2)
        {
            _viewModel.LeftPath = e.Args[0];
            _viewModel.RightPath = e.Args[1];
        }

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();

        if (_viewModel.CompareCommand.CanExecute(null))
            _viewModel.CompareCommand.Execute(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.PersistSettings();
        _viewModel?.Dispose();
        base.OnExit(e);
    }
}
