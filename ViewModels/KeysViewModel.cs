using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDiff.Models;
using FolderDiff.Services;

namespace FolderDiff.ViewModels;

public partial class KeysViewModel : ObservableObject
{
    private readonly ImageTransferService _transfer;
    private readonly IDialogService _dialogs;

    public KeysViewModel(ImageTransferService transfer, IDialogService dialogs)
    {
        _transfer = transfer;
        _dialogs = dialogs;
        _transfer.PropertyChanged += OnTransferChanged;
    }

    public KeyStatus Status => _transfer.Status;

    public string KeyFilePath => _transfer.KeyFilePath;

    public bool KeyFileExists => _transfer.KeyFileExists;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCodeCommand))]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _machineA = "machine-a";

    [ObservableProperty]
    private string _machineB = "machine-b";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyGeneratedACommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyGeneratedBCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseGeneratedACommand))]
    [NotifyCanExecuteChangedFor(nameof(SavePairCommand))]
    private string _generatedA = string.Empty;

    [ObservableProperty]
    private string _generatedB = string.Empty;

    [ObservableProperty]
    private string _pemFolder = string.Empty;

    [ObservableProperty]
    private string _pemPassword = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    public void ApplySettings(AppSettings settings)
    {
        _ = settings;
        Load();
    }

    public void WriteSettings(AppSettings settings) => _ = settings;

    public void Load()
    {
        var fromFile = _transfer.TryAutoLoad();
        if (fromFile.IsValid)
        {
            Code = _transfer.CurrentCode;
            Message = $"Ключ узято з файлу: {fromFile.Source}";
            return;
        }


        Message = $"Ключа немає. Покладіть файл {Path.GetFileName(KeyFilePath)} поруч з програмою або вставте рядок нижче.";
    }

    private bool CanApplyCode() => !string.IsNullOrWhiteSpace(Code);

    private bool HasGenerated() => GeneratedA.Length > 0 && GeneratedB.Length > 0;

    private bool CanUsePem() => !string.IsNullOrWhiteSpace(PemFolder);

    [RelayCommand]
    private void InstallKeyFile()
    {
        var picked = _dialogs.PickFile("Файл ключа imgkeys.txt", "Ключ (*.txt)|*.txt|Усі файли (*.*)|*.*");
        if (picked is null)
            return;

        var status = _transfer.InstallKeyFile(picked);
        if (status.IsValid)
        {
            Code = _transfer.CurrentCode;
            Message = $"Файл покладено поруч з програмою. {status.Description}";
        }
        else
        {
            Message = status.Description;
        }
    }

    [RelayCommand]
    private void ReloadFromFile() => Load();

    [RelayCommand]
    private void OpenKeyFolder()
    {
        var folder = Path.GetDirectoryName(KeyFilePath);
        if (folder is not null && Directory.Exists(folder))
            _dialogs.OpenInShell(folder);
    }

    [RelayCommand(CanExecute = nameof(CanApplyCode))]
    private void ApplyCode()
    {
        var status = _transfer.SetKey(Code.Trim());
        Message = status.IsValid
            ? $"Ключ прийнято. {status.Description}"
            : $"Ключ не прийнято. {status.Description}";
    }

    [RelayCommand(CanExecute = nameof(CanApplyCode))]
    private void CopyCode() => _dialogs.CopyToClipboard(Code);

    [RelayCommand]
    private void SaveCodeAsFile()
    {
        if (string.IsNullOrWhiteSpace(Code))
            return;

        try
        {
            File.WriteAllText(KeyFilePath, Code.Trim());
            Message = $"Записано у {KeyFilePath}";
            OnPropertyChanged(nameof(KeyFileExists));
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void CreatePair()
    {
        try
        {
            var (codeA, codeB) = _transfer.CreatePair(MachineA.Trim(), MachineB.Trim());
            GeneratedA = codeA;
            GeneratedB = codeB;
            Message = "Пару створено. Один ключ лишається цій машині, другий передайте на другу машину.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void CopyGeneratedA() => _dialogs.CopyToClipboard(GeneratedA);

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void CopyGeneratedB() => _dialogs.CopyToClipboard(GeneratedB);

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void UseGeneratedA()
    {
        Code = GeneratedA;
        ApplyCode();
    }

    [RelayCommand(CanExecute = nameof(HasGenerated))]
    private void SavePair()
    {
        var path = _dialogs.PickSaveFile("Зберегти пару ключів", "img-key-pair.txt");
        if (path is null)
            return;

        try
        {
            var text = string.Join(Environment.NewLine, new[]
            {
                $"# {MachineA}", GeneratedA, string.Empty, $"# {MachineB}", GeneratedB, string.Empty
            });
            File.WriteAllText(path, text);
            Message = $"Збережено у {path}";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void BrowsePemFolder()
    {
        var picked = _dialogs.PickFolder("Папка з PEM-ключами", PemFolder);
        if (picked is not null)
        {
            PemFolder = picked;
            UsePem();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsePem))]
    private void UsePem()
    {
        var status = _transfer.UsePemFolder(PemFolder, PemPassword);
        Message = status.IsValid ? status.Description : $"PEM-ключі не підійшли. {status.Description}";
    }

    [RelayCommand]
    private void GeneratePem()
    {
        var folder = _dialogs.PickFolder("Куди створити PEM-ключі", PemFolder);
        if (folder is null)
            return;

        try
        {
            _transfer.GeneratePemKeysAsync(folder, PemPassword).GetAwaiter().GetResult();
            PemFolder = folder;
            Message = $"Створено чотири PEM-файли у {folder}";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    private void OnTransferChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(KeyFileExists));
    }
}
