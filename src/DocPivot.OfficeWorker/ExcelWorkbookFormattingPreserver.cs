using System.IO.Compression;
using System.Xml.Linq;

namespace DocPivot.OfficeWorker;

internal sealed record ExcelWorksheetFormattingSource(string WorkbookPath, int WorksheetPosition, int TargetPosition);

internal static class ExcelWorkbookFormattingPreserver
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void Restore(string outputPath, IReadOnlyList<ExcelWorksheetFormattingSource> sources)
    {
        using var output = ZipFile.Open(outputPath, ZipArchiveMode.Update);
        var outputStyles = Read(output, "xl/styles.xml");
        var outputSheets = WorksheetPaths(output);
        var imported = new Dictionary<string, ImportedStyles>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var targetSheetPath = outputSheets[source.TargetPosition - 1];
            using var input = ZipFile.OpenRead(source.WorkbookPath);
            if (!imported.TryGetValue(source.WorkbookPath, out var styles))
            {
                var theme = ReadOptional(input, "xl/theme/theme1.xml");
                styles = ImportStyles(Read(input, "xl/styles.xml"), outputStyles, theme, imported.Count);
                imported.Add(source.WorkbookPath, styles);
            }

            var sourceSheetPath = WorksheetPaths(input)[source.WorksheetPosition - 1];
            var sourceSheet = Read(input, sourceSheetPath);
            var targetSheet = Read(output, targetSheetPath);
            RestoreSheet(sourceSheet, targetSheet, styles, ReadOptional(input, "xl/sharedStrings.xml"));
            RestoreTables(input, sourceSheetPath, output, targetSheetPath, styles);
            Write(output, targetSheetPath, targetSheet);
            RestoreRelatedThemes(input, sourceSheetPath, output, targetSheetPath, styles.Theme, new HashSet<string>(StringComparer.Ordinal));
        }

        Write(output, "xl/styles.xml", outputStyles);
    }

    private static ImportedStyles ImportStyles(XDocument source, XDocument target, XDocument? theme, int sourceIndex)
    {
        var sourceRoot = source.Root!;
        var targetRoot = target.Root!;
        ResolveTheme(sourceRoot, theme);
        var numberFormats = new Dictionary<int, int>();
        var targetNumberFormats = Collection(targetRoot, "numFmts");
        var nextNumberFormat = Math.Max(164, targetNumberFormats.Elements().Select(element => (int)element.Attribute("numFmtId")!).DefaultIfEmpty(163).Max() + 1);
        foreach (var format in sourceRoot.Element(Spreadsheet + "numFmts")?.Elements() ?? [])
        {
            var replacement = new XElement(format);
            numberFormats.Add((int)format.Attribute("numFmtId")!, nextNumberFormat);
            replacement.SetAttributeValue("numFmtId", nextNumberFormat++);
            targetNumberFormats.Add(replacement);
        }

        UpdateCount(targetNumberFormats);
        var fontOffset = AppendCollection(sourceRoot, targetRoot, "fonts");
        var fillOffset = AppendCollection(sourceRoot, targetRoot, "fills");
        var borderOffset = AppendCollection(sourceRoot, targetRoot, "borders");
        var styleOffset = Collection(targetRoot, "cellStyleXfs").Elements().Count();
        var cellOffset = Collection(targetRoot, "cellXfs").Elements().Count();
        foreach (var name in new[] { "cellStyleXfs", "cellXfs" })
        {
            var collection = Collection(targetRoot, name);
            foreach (var sourceFormat in sourceRoot.Element(Spreadsheet + name)!.Elements())
            {
                var format = new XElement(sourceFormat);
                Offset(format, "fontId", fontOffset);
                Offset(format, "fillId", fillOffset);
                Offset(format, "borderId", borderOffset);
                if (format.Attribute("xfId") is not null)
                {
                    Offset(format, "xfId", styleOffset);
                }

                var numberFormat = (int?)format.Attribute("numFmtId") ?? 0;
                if (numberFormats.TryGetValue(numberFormat, out var replacement))
                {
                    format.SetAttributeValue("numFmtId", replacement);
                }

                collection.Add(format);
            }

            UpdateCount(collection);
        }

        var cellStyles = Collection(targetRoot, "cellStyles");
        foreach (var sourceStyle in sourceRoot.Element(Spreadsheet + "cellStyles")?.Elements() ?? [])
        {
            var style = new XElement(sourceStyle);
            style.SetAttributeValue("name", $"DocPivot_{sourceIndex}_{(string?)style.Attribute("name")}");
            style.Attribute("builtinId")?.Remove();
            style.Attribute("customBuiltin")?.Remove();
            Offset(style, "xfId", styleOffset);
            cellStyles.Add(style);
        }

        UpdateCount(cellStyles);
        var differentialOffset = AppendCollection(sourceRoot, targetRoot, "dxfs");
        var tableStyleNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var tableStyles = Collection(targetRoot, "tableStyles");
        foreach (var sourceStyle in sourceRoot.Element(Spreadsheet + "tableStyles")?.Elements() ?? [])
        {
            var style = new XElement(sourceStyle);
            var sourceName = (string)style.Attribute("name")!;
            var targetName = $"DocPivot_{sourceIndex}_{sourceName}";
            tableStyleNames.Add(sourceName, targetName);
            style.SetAttributeValue("name", targetName);
            foreach (var element in style.Elements())
            {
                Offset(element, "dxfId", differentialOffset);
            }

            tableStyles.Add(style);
        }

        UpdateCount(tableStyles);
        return new ImportedStyles(cellOffset, differentialOffset, tableStyleNames, theme);
    }

    private static void RestoreSheet(XDocument source, XDocument target, ImportedStyles styles, XDocument? sharedStrings)
    {
        var sourceRoot = source.Root!;
        var targetRoot = target.Root!;
        var strings = sharedStrings?.Root!.Elements(Spreadsheet + "si").ToArray() ?? [];
        var sourceRows = sourceRoot.Element(Spreadsheet + "sheetData")!.Elements().ToDictionary(row => (string)row.Attribute("r")!, StringComparer.Ordinal);
        foreach (var row in targetRoot.Element(Spreadsheet + "sheetData")!.Elements())
        {
            if (!sourceRows.TryGetValue((string)row.Attribute("r")!, out var sourceRow))
            {
                continue;
            }

            RestoreStyle(sourceRow, row, "s", styles.CellOffset, always: false);
            foreach (var attribute in new[] { "ht", "customHeight", "hidden", "outlineLevel", "collapsed", "thickTop", "thickBot" })
            {
                row.SetAttributeValue(attribute, (string?)sourceRow.Attribute(attribute));
            }
            var cells = sourceRow.Elements(Spreadsheet + "c").ToDictionary(cell => (string)cell.Attribute("r")!, StringComparer.Ordinal);
            foreach (var cell in row.Elements(Spreadsheet + "c"))
            {
                if (cells.TryGetValue((string)cell.Attribute("r")!, out var sourceCell))
                {
                    RestoreStyle(sourceCell, cell, "s", styles.CellOffset, always: true);
                    RestoreRichText(sourceCell, cell, strings, styles.Theme);
                }
            }
        }

        // Column definitions come back exactly as the source wrote them (widths included); only their
        // style indexes move to the imported copies. Columns the source never defined are left alone.
        targetRoot.Element(Spreadsheet + "cols")?.Remove();
        if (sourceRoot.Element(Spreadsheet + "cols") is { } sourceColumns)
        {
            var columns = new XElement(sourceColumns);
            foreach (var column in columns.Elements(Spreadsheet + "col"))
            {
                RestoreStyle(column, column, "style", styles.CellOffset, always: false);
            }

            targetRoot.Element(Spreadsheet + "sheetData")!.AddBeforeSelf(columns);
        }

        if (sourceRoot.Element(Spreadsheet + "sheetFormatPr") is { } sourceFormat)
        {
            var targetFormat = targetRoot.Element(Spreadsheet + "sheetFormatPr");
            if (targetFormat is null)
            {
                (targetRoot.Element(Spreadsheet + "cols") ?? targetRoot.Element(Spreadsheet + "sheetData")!)
                    .AddBeforeSelf(new XElement(sourceFormat));
            }
            else
            {
                targetFormat.ReplaceWith(new XElement(sourceFormat));
            }
        }

        ResolveTheme(sourceRoot, styles.Theme);
        var sourceRules = sourceRoot.Descendants(Spreadsheet + "cfRule").ToArray();
        var targetRules = targetRoot.Descendants(Spreadsheet + "cfRule").ToArray();
        for (var index = 0; index < Math.Min(sourceRules.Length, targetRules.Length); index++)
        {
            if (sourceRules[index].Attribute("dxfId") is not null)
            {
                RestoreStyle(sourceRules[index], targetRules[index], "dxfId", styles.DifferentialOffset, always: true);
            }

            var sourceColors = sourceRules[index].Descendants().Where(element => element.Name.LocalName == "color").ToArray();
            var targetColors = targetRules[index].Descendants().Where(element => element.Name.LocalName == "color").ToArray();
            for (var colorIndex = 0; colorIndex < Math.Min(sourceColors.Length, targetColors.Length); colorIndex++)
            {
                targetColors[colorIndex].ReplaceWith(new XElement(sourceColors[colorIndex]));
            }
        }

        var sourceTabColor = sourceRoot.Element(Spreadsheet + "sheetPr")?.Element(Spreadsheet + "tabColor");
        var targetTabColor = targetRoot.Element(Spreadsheet + "sheetPr")?.Element(Spreadsheet + "tabColor");
        if (sourceTabColor is not null && targetTabColor is not null)
        {
            targetTabColor.ReplaceWith(new XElement(sourceTabColor));
        }

        ResolveTheme(targetRoot, styles.Theme);
    }

    private static void RestoreRichText(XElement sourceCell, XElement targetCell, XElement[] sharedStrings, XDocument? theme)
    {
        var text = (string?)sourceCell.Attribute("t") switch
        {
            "s" => sharedStrings[(int)sourceCell.Element(Spreadsheet + "v")!],
            "inlineStr" => sourceCell.Element(Spreadsheet + "is"),
            _ => null,
        };
        if (text?.Element(Spreadsheet + "r") is null)
        {
            return;
        }

        // One shared string can be used by sheets with different themes after merging.
        var inlineText = new XElement(Spreadsheet + "is", text.Nodes());
        ResolveTheme(inlineText, theme);
        targetCell.SetAttributeValue("t", "inlineStr");
        targetCell.Element(Spreadsheet + "v")?.Remove();
        targetCell.Element(Spreadsheet + "is")?.Remove();
        targetCell.Add(inlineText);
    }

    private static void RestoreTables(
        ZipArchive source,
        string sourceSheet,
        ZipArchive target,
        string targetSheet,
        ImportedStyles styles)
    {
        var sourceTables = RelatedParts(source, sourceSheet, "/table");
        var targetTables = RelatedParts(target, targetSheet, "/table");
        for (var index = 0; index < Math.Min(sourceTables.Length, targetTables.Length); index++)
        {
            var sourceTable = Read(source, sourceTables[index]);
            var targetTable = Read(target, targetTables[index]);
            var sourceStyle = sourceTable.Root!.Element(Spreadsheet + "tableStyleInfo");
            var targetStyle = targetTable.Root!.Element(Spreadsheet + "tableStyleInfo");
            if (sourceStyle is not null && targetStyle is not null &&
                styles.TableStyleNames.TryGetValue((string)sourceStyle.Attribute("name")!, out var name))
            {
                targetStyle.SetAttributeValue("name", name);
            }

            foreach (var sourceElement in sourceTable.Root.DescendantsAndSelf())
            {
                foreach (var attribute in sourceElement.Attributes().Where(attribute => attribute.Name.LocalName.EndsWith("DxfId", StringComparison.Ordinal)))
                {
                    var targetElement = sourceElement == sourceTable.Root
                        ? targetTable.Root
                        : targetTable.Root.Descendants(sourceElement.Name).FirstOrDefault(element =>
                            (string?)element.Attribute("id") == (string?)sourceElement.Attribute("id"));
                    targetElement?.SetAttributeValue(attribute.Name, (int)attribute + styles.DifferentialOffset);
                }
            }

            Write(target, targetTables[index], targetTable);
        }
    }

    private static string[] RelatedParts(ZipArchive package, string partPath, string typeSuffix) =>
        ReadOptional(package, RelationshipPath(partPath))?.Root!.Elements()
            .Where(element => ((string)element.Attribute("Type")!).EndsWith(typeSuffix, StringComparison.Ordinal))
            .Select(element => ResolvePart(partPath, (string)element.Attribute("Target")!)).ToArray() ?? [];

    private static void RestoreStyle(XElement source, XElement target, string attribute, int offset, bool always)
    {
        if (always || source.Attribute(attribute) is not null)
        {
            target.SetAttributeValue(attribute, ((int?)source.Attribute(attribute) ?? 0) + offset);
        }
    }

    private static void ResolveTheme(XElement root, XDocument? theme)
    {
        var colorScheme = theme?.Descendants(Drawing + "clrScheme").FirstOrDefault();
        var colors = new[] { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
        if (colorScheme is not null)
        {
            foreach (var element in root.DescendantsAndSelf().Where(element => element.Attribute("theme") is not null))
            {
                var index = (int)element.Attribute("theme")!;
                var color = ThemeColor(colorScheme, colors[index]);
                if (color is not null)
                {
                    element.SetAttributeValue("rgb", "FF" + color);
                    element.Attribute("theme")!.Remove();
                    element.Attribute("indexed")?.Remove();
                    element.Attribute("auto")?.Remove();
                }
            }

            foreach (var element in root.DescendantsAndSelf(Drawing + "schemeClr").ToArray())
            {
                var name = (string)element.Attribute("val")!;
                name = name switch { "bg1" => "lt1", "tx1" => "dk1", "bg2" => "lt2", "tx2" => "dk2", _ => name };
                var color = ThemeColor(colorScheme, name);
                if (color is not null)
                {
                    element.ReplaceWith(new XElement(Drawing + "srgbClr", new XAttribute("val", color), element.Elements()));
                }
            }
        }

        foreach (var font in root.DescendantsAndSelf().Where(element => element.Name == Spreadsheet + "font" || element.Name == Spreadsheet + "rPr"))
        {
            var scheme = font.Element(Spreadsheet + "scheme");
            // Excel saves the effective face alongside scheme. Keep that face, including
            // East Asian fonts, rather than replacing it with the theme's Latin face.
            scheme?.Remove();
        }

        foreach (var typeface in root.DescendantsAndSelf().Attributes("typeface").Where(attribute => attribute.Value.StartsWith('+')))
        {
            var family = typeface.Value.StartsWith("+mj", StringComparison.Ordinal) ? "majorFont" : "minorFont";
            var script = typeface.Value.EndsWith("-ea", StringComparison.Ordinal) ? "ea" : typeface.Value.EndsWith("-cs", StringComparison.Ordinal) ? "cs" : "latin";
            var resolved = (string?)theme?.Descendants(Drawing + family).Elements(Drawing + script).FirstOrDefault()?.Attribute("typeface");
            if (!string.IsNullOrEmpty(resolved))
            {
                typeface.Value = resolved;
            }
        }
    }

    private static string? ThemeColor(XElement scheme, string name)
    {
        var color = scheme.Element(Drawing + name)?.Elements().FirstOrDefault();
        return (string?)color?.Attribute(color.Name.LocalName == "sysClr" ? "lastClr" : "val");
    }

    private static void RestoreRelatedThemes(
        ZipArchive source,
        string sourcePart,
        ZipArchive target,
        string targetPart,
        XDocument? theme,
        HashSet<string> visited)
    {
        if (theme is null || !visited.Add(targetPart))
        {
            return;
        }

        foreach (var type in new[] { "/drawing", "/chart" })
        {
            var sourceParts = RelatedParts(source, sourcePart, type);
            var targetParts = RelatedParts(target, targetPart, type);
            for (var index = 0; index < Math.Min(sourceParts.Length, targetParts.Length); index++)
            {
                var effectiveTheme = new XDocument(theme);
                var sourceOverride = RelatedParts(source, sourceParts[index], "/themeOverride").FirstOrDefault();
                if (sourceOverride is not null)
                {
                    var elements = effectiveTheme.Root!.Element(Drawing + "themeElements")!;
                    foreach (var replacement in Read(source, sourceOverride).Root!.Elements())
                    {
                        elements.Element(replacement.Name)?.Remove();
                        elements.Add(new XElement(replacement));
                    }
                }

                var related = Read(target, targetParts[index]);
                ResolveTheme(related.Root!, effectiveTheme);
                Write(target, targetParts[index], related);
                if (type == "/chart")
                {
                    WriteChartTheme(target, targetParts[index], effectiveTheme);
                }

                RestoreRelatedThemes(source, sourceParts[index], target, targetParts[index], effectiveTheme, visited);
            }
        }
    }

    private static void WriteChartTheme(ZipArchive package, string chartPath, XDocument theme)
    {
        var relationshipPath = RelationshipPath(chartPath);
        var relationships = ReadOptional(package, relationshipPath) ?? new XDocument(new XElement(Relationships + "Relationships"));
        var existing = relationships.Root!.Elements().FirstOrDefault(element => ((string?)element.Attribute("Type"))?.EndsWith("/themeOverride", StringComparison.Ordinal) == true);
        var themePath = existing is null
            ? $"xl/theme/docpivot-{Path.GetFileNameWithoutExtension(chartPath)}.xml"
            : ResolvePart(chartPath, (string)existing.Attribute("Target")!);
        if (existing is null)
        {
            relationships.Root.Add(new XElement(Relationships + "Relationship",
                new XAttribute("Id", "rIdDocPivotTheme"),
                new XAttribute("Type", OfficeRelationships + "/themeOverride"),
                new XAttribute("Target", "../theme/" + Path.GetFileName(themePath))));
            Write(package, relationshipPath, relationships);
        }

        Write(package, themePath, new XDocument(new XElement(Drawing + "themeOverride", theme.Root!.Element(Drawing + "themeElements")!.Elements())));
        var types = Read(package, "[Content_Types].xml");
        if (!types.Root!.Elements().Any(element => (string?)element.Attribute("PartName") == "/" + themePath))
        {
            types.Root.Add(new XElement(ContentTypes + "Override", new XAttribute("PartName", "/" + themePath),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.themeOverride+xml")));
            Write(package, "[Content_Types].xml", types);
        }
    }

    private static int AppendCollection(XElement source, XElement target, string name)
    {
        var collection = Collection(target, name);
        var offset = collection.Elements().Count();
        collection.Add(source.Element(Spreadsheet + name)?.Elements().Select(static element => new XElement(element)));
        UpdateCount(collection);
        return offset;
    }

    private static XElement Collection(XElement root, string name)
    {
        var existing = root.Element(Spreadsheet + name);
        if (existing is not null)
        {
            return existing;
        }

        var names = new[] { "numFmts", "fonts", "fills", "borders", "cellStyleXfs", "cellXfs", "cellStyles", "dxfs", "tableStyles", "colors", "extLst" };
        var element = new XElement(Spreadsheet + name);
        var next = root.Elements().FirstOrDefault(child => Array.IndexOf(names, child.Name.LocalName) > Array.IndexOf(names, name));
        if (next is null)
        {
            root.Add(element);
        }
        else
        {
            next.AddBeforeSelf(element);
        }

        return element;
    }

    private static void UpdateCount(XElement collection) => collection.SetAttributeValue("count", collection.Elements().Count());

    private static void Offset(XElement element, string attribute, int offset) => element.SetAttributeValue(attribute, ((int?)element.Attribute(attribute) ?? 0) + offset);

    private static string[] WorksheetPaths(ZipArchive package)
    {
        var relationships = Read(package, "xl/_rels/workbook.xml.rels").Root!.Elements()
            .ToDictionary(element => (string)element.Attribute("Id")!, element => (string)element.Attribute("Target")!, StringComparer.Ordinal);
        return Read(package, "xl/workbook.xml").Root!.Element(Spreadsheet + "sheets")!.Elements()
            .Select(sheet => ResolvePart("xl/workbook.xml", relationships[(string)sheet.Attribute(OfficeRelationships + "id")!])).ToArray();
    }

    private static string RelationshipPath(string partPath) =>
        (Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? string.Empty) + "/_rels/" + Path.GetFileName(partPath) + ".rels";

    private static string ResolvePart(string partPath, string target) => new Uri(new Uri("http://package/" + partPath), target).AbsolutePath.TrimStart('/');

    private static XDocument Read(ZipArchive package, string name)
    {
        using var stream = package.GetEntry(name)!.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static XDocument? ReadOptional(ZipArchive package, string name) => package.GetEntry(name) is null ? null : Read(package, name);

    private static void Write(ZipArchive package, string name, XDocument document)
    {
        package.GetEntry(name)?.Delete();
        using var stream = package.CreateEntry(name, CompressionLevel.Optimal).Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private sealed record ImportedStyles(
        int CellOffset,
        int DifferentialOffset,
        IReadOnlyDictionary<string, string> TableStyleNames,
        XDocument? Theme);
}
