using CpuTempWidget.Services;

namespace CpuTempWidget.Core;

public static class ModuleRegistry
{
    private static IReadOnlyList<IPulseModule>? _modules;

    public static IReadOnlyList<IPulseModule> All => _modules ??= Build();

    public static IPulseModule? Get(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<IPulseCommand> AllCommands() =>
        All.SelectMany(m => m.GetCommands());

    private static IReadOnlyList<IPulseModule> Build() =>
    [
        new CatalogModule("performance", "Performance", "\uE9D9"),
        new CatalogModule("hardware", "Hardware", "\uE950"),
        new CatalogModule("network", "Network", "\uE968"),
        new CatalogModule("maintenance", "Maintenance", "\uEA79"),
        new CatalogModule("applications", "Applications", "\uE71D"),
        new CatalogModule("security", "Security", "\uE72E"),
        new CatalogModule("storage", "Storage", "\uEDA2"),
        new CatalogModule("developer", "Developer", "\uE943"),
        new CatalogModule("windows", "Windows", "\uE8FC"),
        new CatalogModule("settings", "Settings", "\uE713"),
        new CatalogModule("battery", "Battery", "\uE83F"),
    ];
}

/// <summary>Module backed by PulseCatalog actions for a category id.</summary>
public sealed class CatalogModule : IPulseModule
{
    public CatalogModule(string id, string label, string glyph)
    {
        Id = id;
        Label = label;
        Glyph = glyph;
    }

    public string Id { get; }
    public string Label { get; }
    public string Glyph { get; }

    public IReadOnlyList<IPulseCommand> GetCommands() =>
        PulseCatalog.CommandsFor(Id).ToList();
}
