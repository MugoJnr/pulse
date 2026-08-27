namespace CpuTempWidget.Core;

public enum SearchResultKind
{
    Application,
    Setting,
    ControlPanel,
    QuickAction,
    Process,
    File,
    Folder,
    Command,
    Favorite,
    Recent,
    Category
}

public interface IPulseCommand
{
    string Id { get; }
    string Title { get; }
    string Subtitle { get; }
    string Glyph { get; }
    string ModuleId { get; }
    SearchResultKind Kind { get; }
    IReadOnlyList<string> Keywords { get; }
    bool IsDestructive { get; }
    bool RequiresElevation { get; }
    void Execute();
}

public sealed class PulseCommand : IPulseCommand
{
    private readonly Action _execute;

    public PulseCommand(
        string id,
        string title,
        string subtitle,
        string glyph,
        string moduleId,
        Action execute,
        SearchResultKind kind = SearchResultKind.Command,
        bool isDestructive = false,
        bool requiresElevation = false,
        params string[] keywords)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        ModuleId = moduleId;
        Kind = kind;
        IsDestructive = isDestructive;
        RequiresElevation = requiresElevation;
        Keywords = keywords;
        _execute = execute;
    }

    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public string ModuleId { get; }
    public SearchResultKind Kind { get; }
    public IReadOnlyList<string> Keywords { get; }
    public bool IsDestructive { get; }
    public bool RequiresElevation { get; }
    public void Execute() => _execute();
}

public interface IPulseModule
{
    string Id { get; }
    string Label { get; }
    string Glyph { get; }
    IReadOnlyList<IPulseCommand> GetCommands();
}
