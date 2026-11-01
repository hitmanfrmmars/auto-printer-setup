using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPrinterSetup;

/// <summary>Printers this app added, kept in %AppData%\AutoPrinterSetup\printers.json so they can be repaired later.</summary>
public static class PrinterStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPrinterSetup", "printers.json");

    public static List<SavedPrinter> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<SavedPrinter>>(File.ReadAllText(FilePath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static SavedPrinter? Find(string windowsName) =>
        Load().FirstOrDefault(p => p.WindowsName.Equals(windowsName, StringComparison.OrdinalIgnoreCase));

    public static void Upsert(SavedPrinter printer)
    {
        var all = Load();
        all.RemoveAll(p => p.WindowsName.Equals(printer.WindowsName, StringComparison.OrdinalIgnoreCase));
        all.Add(printer);
        Save(all);
    }

    public static void Remove(string windowsName)
    {
        var all = Load();
        if (all.RemoveAll(p => p.WindowsName.Equals(windowsName, StringComparison.OrdinalIgnoreCase)) > 0) Save(all);
    }

    static void Save(List<SavedPrinter> printers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(printers, JsonOptions));
    }
}
