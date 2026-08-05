using System.Text.Json;

namespace DocPivot.Infrastructure.Pdf;

internal sealed record QpdfJsonInspection(
    int PageCount,
    bool IsEncrypted,
    bool HasSignatureFields);

internal static class QpdfJsonInspector
{
    public static QpdfJsonInspection Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("qpdf JSON did not contain a pages array.");
        }

        if (!root.TryGetProperty("encrypt", out var encrypt) ||
            !encrypt.TryGetProperty("encrypted", out var encrypted) ||
            encrypted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException("qpdf JSON did not contain an encryption status.");
        }

        var hasSignatureFields = false;
        if (root.TryGetProperty("acroform", out var acroform) &&
            acroform.TryGetProperty("fields", out var fields) &&
            fields.ValueKind == JsonValueKind.Array)
        {
            hasSignatureFields = fields.EnumerateArray().Any(IsSignatureField);
        }

        return new QpdfJsonInspection(
            pages.GetArrayLength(),
            encrypted.GetBoolean(),
            hasSignatureFields);
    }

    private static bool IsSignatureField(JsonElement field)
    {
        if (!field.TryGetProperty("fieldtype", out var fieldType) ||
            fieldType.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = fieldType.GetString();
        return string.Equals(value, "/Sig", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Sig", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Signature", StringComparison.OrdinalIgnoreCase);
    }
}
