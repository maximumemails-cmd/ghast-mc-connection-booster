using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghast.Services;

namespace Ghast.ViewModels;

public partial class PingViewModel : ObservableObject
{
    private readonly PingService _ping;

    public PingViewModel(PingService ping) => _ping = ping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private string _address = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    [ObservableProperty] private string? _progressText;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyPropertyChangedFor(nameof(ResolvedText))]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    [NotifyPropertyChangedFor(nameof(PlayersText))]
    private PingReport? _report;

    public bool HasReport => Report is not null;

    public string ResolvedText => Report is { } r
        ? $"{r.Host}:{r.Port}" + (r.ResolvedVia == "SRV record" ? "  (via SRV record)" : "")
        : "";

    public string ScoreText => Report is { } r ? $"{r.Score} / 100" : "";

    public string PlayersText => Report is { PlayersOnline: { } on, PlayersMax: { } max }
        ? $"{on:N0} / {max:N0} players online"
        : "";

    private bool CanTest() => !IsTesting && !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        ErrorText = null;
        Report = null;
        ProgressText = "Connecting…";
        var progress = new Progress<string>(msg => ProgressText = msg);

        try
        {
            Report = await Task.Run(() => _ping.TestAsync(Address, progress));
        }
        catch (PingTestException ex)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.Error("ping test", ex);
            ErrorText = "Something went wrong running the test — see log.txt for details.";
        }
        finally
        {
            ProgressText = null;
            IsTesting = false;
        }
    }
}
