using CpuTempWidget.Core;

namespace CpuTempWidget.Services;

public enum SearchKind
{
    Setting,
    Tool,
    App,
    Command,
    Category,
    Process,
    File,
    Folder,
    Favorite,
    Recent,
    QuickAction,
    ControlPanel
}

public sealed class SearchItem
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Glyph { get; init; }
    public required SearchKind Kind { get; init; }
    public required Action Execute { get; init; }
    public string[] Keywords { get; init; } = [];
    public IPulseCommand? Command { get; init; }
    public string? Badge { get; init; }
}

public static class SearchService
{
    public static IReadOnlyList<SearchItem> Search(string query, int limit = 32)
    {
        return SearchEngine.Search(query, limit).Select(h => new SearchItem
        {
            Title = h.Command.Title,
            Subtitle = h.Badge is null ? h.Command.Subtitle : $"{h.Badge} · {h.Command.Subtitle}",
            Glyph = h.Command.Glyph,
            Kind = Map(h.Command.Kind),
            Execute = () => CommandDispatcher.Execute(h.Command),
            Keywords = h.Command.Keywords.ToArray(),
            Command = h.Command,
            Badge = h.Badge
        }).ToList();
    }

    private static SearchKind Map(SearchResultKind k) => k switch
    {
        SearchResultKind.Application => SearchKind.App,
        SearchResultKind.Setting => SearchKind.Setting,
        SearchResultKind.ControlPanel => SearchKind.ControlPanel,
        SearchResultKind.QuickAction => SearchKind.QuickAction,
        SearchResultKind.Process => SearchKind.Process,
        SearchResultKind.File => SearchKind.File,
        SearchResultKind.Folder => SearchKind.Folder,
        SearchResultKind.Favorite => SearchKind.Favorite,
        SearchResultKind.Recent => SearchKind.Recent,
        SearchResultKind.Category => SearchKind.Category,
        _ => SearchKind.Command
    };
}
