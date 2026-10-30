using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Anvil.App.Services;
using Anvil.Core.Logs;
using Anvil.Core.Paths;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

public enum LogLevel { Info, Warning, Error }

/// <summary>One line as written by its program; the level is read from the words Apache, PHP and MariaDB use.</summary>
public sealed partial record LogLine(string Source, string Text, LogLevel Level)
{
    [GeneratedRegex(@"\b(error|fatal|crit|critical|alert|emerg)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWord();

    [GeneratedRegex(@"\b(warn|warning)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningWord();

    public static LogLevel LevelOf(string text) =>
        ErrorWord().IsMatch(text) ? LogLevel.Error : WarningWord().IsMatch(text) ? LogLevel.Warning : LogLevel.Info;

    public bool IsError => Level == LogLevel.Error;
    public bool IsWarning => Level == LogLevel.Warning;
}

/// <summary>A source chip; <see cref="Source"/> is null for "All".</summary>
public sealed partial class SourceChip(string label, LogSource? source) : ObservableObject
{
    public string Label { get; } = label;
    public LogSource? Source { get; } = source;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>The Logs page: every log under data\logs, one at a time or all together, followed live.</summary>
public sealed partial class LogsViewModel : ObservableObject, IDisposable
{
    private const int Keep = 2000;
    private readonly AppPaths _paths;
    private readonly EditorLauncher _editor;
    private readonly Action<Action> _ui;
    private readonly List<LogLine> _buffer = [];
    private CancellationTokenSource? _following;

    public LogsViewModel(AppPaths paths, EditorLauncher editor, Action<Action>? ui = null)
    {
        _paths = paths;
        _editor = editor;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
    }

    public ObservableCollection<SourceChip> Sources { get; } = [];
    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    public partial SourceChip? Selected { get; set; }

    [ObservableProperty]
    public partial bool Follow { get; set; } = true;

    [ObservableProperty]
    public partial bool ErrorsOnly { get; set; }

    /// <summary>The logs Clear could not empty, or null.</summary>
    [ObservableProperty]
    public partial string? ClearMessage { get; set; }

    /// <summary>Finds the logs again (new sites or PHP versions) and reloads the selected one.</summary>
    public void Refresh()
    {
        var selectedId = Selected?.Source?.Id;
        Sources.Clear();
        Sources.Add(new SourceChip("All", null));
        foreach (var source in LogSources.Discover(_paths)) Sources.Add(new SourceChip(source.Label, source));
        Select(Sources.FirstOrDefault(c => c.Source is not null && c.Source.Id == selectedId) ?? Sources[0]);
    }

    public void Dispose() => StopFollowing();

    [RelayCommand]
    private void Select(SourceChip chip)
    {
        Selected = chip;
        ClearMessage = null;
        foreach (var c in Sources) c.IsSelected = c == chip;
        Load();
    }

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile()
    {
        if (Selected?.Source is { } source && File.Exists(source.Path)) _editor.Open(source.Path);
    }

    private bool CanOpenFile() => Selected?.Source is not null;

    [RelayCommand]
    private void OpenFolder() => EditorLauncher.OpenFolder(_paths.Logs);

    /// <summary>Empties the shown log, or every log while All is selected.</summary>
    [RelayCommand]
    private void Clear()
    {
        var kept = LogSources.Clear(Active);
        ClearMessage = kept.Count == 0 ? null
            : $"{string.Join(", ", kept.Select(Path.GetFileName))} {(kept.Count == 1 ? "is" : "are")} held open by the program writing {(kept.Count == 1 ? "it" : "them")}, so {(kept.Count == 1 ? "it was" : "they were")} not cleared.";
        Load();
    }

    partial void OnFollowChanged(bool value)
    {
        if (value) Load();
        else StopFollowing();
    }

    partial void OnErrorsOnlyChanged(bool value) => Rebuild();

    private IEnumerable<LogSource> Active =>
        Selected?.Source is { } one ? [one] : Sources.Where(c => c.Source is not null).Select(c => c.Source!);

    private void Load()
    {
        StopFollowing();
        _buffer.Clear();
        var perSource = Selected?.Source is null ? 200 : 500;
        foreach (var source in Active)
            _buffer.AddRange(new LogTail(source.Path).ReadLast(perSource).Select(t => new LogLine(source.Label, t, LogLine.LevelOf(t))));
        if (_buffer.Count > Keep) _buffer.RemoveRange(0, _buffer.Count - Keep);
        Rebuild();
        if (Follow) StartFollowing();
    }

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var line in _buffer.Where(Visible)) Lines.Add(line);
    }

    private bool Visible(LogLine line) => !ErrorsOnly || line.Level == LogLevel.Error;

    private void Append(LogLine line)
    {
        _buffer.Add(line);
        if (_buffer.Count > Keep) _buffer.RemoveAt(0);
        if (!Visible(line)) return;
        Lines.Add(line);
        if (Lines.Count > Keep) Lines.RemoveAt(0);
    }

    private void StartFollowing()
    {
        _following = new CancellationTokenSource();
        foreach (var source in Active) _ = FollowAsync(source, _following.Token);
    }

    private void StopFollowing()
    {
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
    }

    private async Task FollowAsync(LogSource source, CancellationToken ct)
    {
        try
        {
            await foreach (var text in new LogTail(source.Path).FollowAsync(ct))
            {
                var line = new LogLine(source.Label, text, LogLine.LevelOf(text));
                _ui(() =>
                {
                    if (!ct.IsCancellationRequested) Append(line);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Warning(e, "Following {Log} stopped", source.Path);
        }
    }
}
