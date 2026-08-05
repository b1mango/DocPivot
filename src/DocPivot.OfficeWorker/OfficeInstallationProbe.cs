using Microsoft.Win32;

namespace DocPivot.OfficeWorker;

internal static class OfficeInstallationProbe
{
    public static (Dictionary<string, bool> Capabilities, Dictionary<string, string> Metadata) Probe()
    {
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["wordComRegistered"] = Type.GetTypeFromProgID("Word.Application") is not null,
            ["excelComRegistered"] = Type.GetTypeFromProgID("Excel.Application") is not null,
        };
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        ReadClickToRunMetadata(metadata);
        return (capabilities, metadata);
    }

    private static void ReadClickToRunMetadata(Dictionary<string, string> metadata)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration",
            writable: false);
        AddRegistryValue(key, "Platform", "officePlatform", metadata);
        AddRegistryValue(key, "VersionToReport", "officeVersion", metadata);
        AddRegistryValue(key, "ProductReleaseIds", "officeProductReleaseIds", metadata);
    }

    private static void AddRegistryValue(
        RegistryKey? key,
        string valueName,
        string metadataName,
        Dictionary<string, string> metadata)
    {
        if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
        {
            metadata[metadataName] = value;
        }
    }
}
