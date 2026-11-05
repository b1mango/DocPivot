using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DocPivot.App.Controls;

/// <summary>
/// Shows a new file name with the part that differs from the original marked, so a
/// rename preview reads at a glance: "<mark>2026-Q3</mark>预算汇总.xlsx".
/// </summary>
public static class RenameHighlight
{
    public static readonly DependencyProperty OriginalProperty = DependencyProperty.RegisterAttached(
        "Original",
        typeof(string),
        typeof(RenameHighlight),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty UpdatedProperty = DependencyProperty.RegisterAttached(
        "Updated",
        typeof(string),
        typeof(RenameHighlight),
        new PropertyMetadata(null, OnChanged));

    public static string? GetOriginal(DependencyObject element) => (string?)element.GetValue(OriginalProperty);

    public static void SetOriginal(DependencyObject element, string? value) => element.SetValue(OriginalProperty, value);

    public static string? GetUpdated(DependencyObject element) => (string?)element.GetValue(UpdatedProperty);

    public static void SetUpdated(DependencyObject element, string? value) => element.SetValue(UpdatedProperty, value);

    /// <summary>Splits <paramref name="updated"/> into unchanged head, changed middle and unchanged tail.</summary>
    public static (string Head, string Changed, string Tail) Split(string original, string updated)
    {
        var prefix = 0;
        var limit = Math.Min(original.Length, updated.Length);
        while (prefix < limit && original[prefix] == updated[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix &&
               original[original.Length - 1 - suffix] == updated[updated.Length - 1 - suffix])
        {
            suffix++;
        }

        return (
            updated[..prefix],
            updated[prefix..(updated.Length - suffix)],
            updated[(updated.Length - suffix)..]);
    }

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock textBlock)
        {
            return;
        }

        var original = GetOriginal(textBlock) ?? string.Empty;
        var updated = GetUpdated(textBlock) ?? string.Empty;
        textBlock.Inlines.Clear();
        if (updated.Length == 0)
        {
            textBlock.Text = original;
            return;
        }

        var (head, changed, tail) = Split(original, updated);
        if (changed.Length == 0 || string.Equals(original, updated, StringComparison.Ordinal))
        {
            textBlock.Text = updated;
            return;
        }

        if (head.Length > 0)
        {
            textBlock.Inlines.Add(new Run(head));
        }

        var mark = new Run(changed);
        mark.SetResourceReference(TextElement.BackgroundProperty, "AccSoftBrush");
        mark.SetResourceReference(TextElement.ForegroundProperty, "AccTextBrush");
        textBlock.Inlines.Add(mark);
        if (tail.Length > 0)
        {
            textBlock.Inlines.Add(new Run(tail));
        }
    }
}
