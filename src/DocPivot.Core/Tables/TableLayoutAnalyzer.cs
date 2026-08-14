using System.Text;

namespace DocPivot.Core.Tables;

public static class TableLayoutAnalyzer
{
    private const int MaximumWordsPerPage = 100_000;
    private const int MaximumColumns = 64;
    private const int HeaderWindowRows = 3;

    public static DocumentTablePage AnalyzePage(
        int pageNumber,
        double pageWidth,
        double pageHeight,
        DocumentTableSourceKind sourceKind,
        IReadOnlyList<DocumentWord> words)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pageWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pageHeight, 0);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count > MaximumWordsPerPage)
        {
            throw new ArgumentOutOfRangeException(nameof(words), "A PDF page contains too many text objects.");
        }

        var usableWords = words
            .Where(static word =>
                !string.IsNullOrWhiteSpace(word.Text) &&
                word.Bounds.IsValid &&
                double.IsFinite(word.Confidence))
            .Select(static word => word with
            {
                Text = NormalizeText(word.Text),
                Confidence = Math.Clamp(word.Confidence, 0, 1),
            })
            .Where(static word => word.Text.Length > 0)
            .ToArray();
        if (usableWords.Length == 0)
        {
            return new DocumentTablePage(
                pageNumber,
                sourceKind,
                pageWidth,
                pageHeight,
                [],
                ["PAGE_HAS_NO_RECOGNIZABLE_TEXT"]);
        }

        var medianHeight = Median(usableWords.Select(static word => word.Bounds.Height));
        var rows = BuildRows(usableWords, medianHeight);
        var segmentedRows = rows
            .Select(row => new SegmentedRow(row, BuildSegments(row, medianHeight)))
            .Where(static row => row.Segments.Count > 0)
            .ToArray();
        var labelValueTable = TryCreateLabelValueTable(pageNumber, sourceKind, rows, out var remainingRows);
        if (labelValueTable is not null)
        {
            var labelValueTables = new List<DocumentTable> { labelValueTable };
            var remainingSegmented = remainingRows
                .Select(row => new SegmentedRow(row, BuildSegments(row, medianHeight)))
                .Where(static row => row.Segments.Count > 0)
                .ToArray();
            var remainingFallback = CreateFallbackTable(pageNumber, sourceKind, remainingSegmented);
            if (remainingFallback is not null)
            {
                labelValueTables.Add(remainingFallback);
            }

            return new DocumentTablePage(
                pageNumber,
                sourceKind,
                pageWidth,
                pageHeight,
                labelValueTables,
                []);
        }

        var columnClusters = BuildColumnClusters(segmentedRows, medianHeight, pageWidth);
        var tables = BuildTables(pageNumber, sourceKind, segmentedRows, columnClusters, medianHeight);
        if (tables.Count > 0)
        {
            tables = AttachHeaderWindow(pageNumber, sourceKind, segmentedRows, tables, medianHeight);
            var pageWarnings = sourceKind == DocumentTableSourceKind.OpticalCharacterRecognition
                ? new[] { "OCR_CONTENT_REQUIRES_REVIEW" }
                : [];
            return new DocumentTablePage(
                pageNumber,
                sourceKind,
                pageWidth,
                pageHeight,
                tables,
                pageWarnings);
        }

        var fallback = CreateFallbackTable(pageNumber, sourceKind, segmentedRows);
        return new DocumentTablePage(
            pageNumber,
            sourceKind,
            pageWidth,
            pageHeight,
            fallback is null ? [] : [fallback],
            ["TABLE_STRUCTURE_UNCERTAIN"]);
    }

    /// <summary>
    /// Attaches up to three sparse "header window" rows (for example the
    /// "检测项目 / 检测依据 / 单位" band above a data grid) to the top of the
    /// first table when they horizontally align with the table columns.
    /// Each row is expanded to the table width and single-segment values are
    /// placed into the column they overlap, so the exported worksheet keeps
    /// the same header band a spreadsheet user expects.
    /// </summary>
    private static List<DocumentTable> AttachHeaderWindow(
        int pageNumber,
        DocumentTableSourceKind sourceKind,
        IReadOnlyList<SegmentedRow> rows,
        List<DocumentTable> tables,
        double medianHeight)
    {
        var firstTable = tables[0];
        if (firstTable.RowCount == 0 || firstTable.SourceKind != sourceKind)
        {
            return tables;
        }

        var tableTop = firstTable.Bounds.Top;
        var headerCandidateRows = rows
            .Where(row => row.Bounds.Bottom <= tableTop + medianHeight * 0.25)
            .OrderByDescending(static row => row.Bounds.Top)
            .Take(HeaderWindowRows)
            .OrderBy(static row => row.Bounds.Top)
            .ToArray();
        if (headerCandidateRows.Length == 0)
        {
            return tables;
        }

        var tableColumns = firstTable.Cells
            .Where(static cell => cell.RowIndex == 0)
            .Select(static cell => cell.Bounds)
            .OrderBy(static bounds => bounds.Left)
            .ToArray();
        if (tableColumns.Length < 2)
        {
            return tables;
        }

        var columnCenters = tableColumns
            .Select(static bounds => (bounds.Left + bounds.Right) / 2)
            .ToArray();
        var headerCells = new List<DocumentTableCell>();
        var newRowCount = headerCandidateRows.Length + firstTable.RowCount;
        for (var headerRow = 0; headerRow < headerCandidateRows.Length; headerRow++)
        {
            var row = headerCandidateRows[headerRow];
            var segments = row.Segments.OrderBy(static segment => segment.Bounds.Left).ToArray();
            if (segments.Length > columnCenters.Length * 3)
            {
                return tables;
            }

            foreach (var segment in segments)
            {
                var nearest = FindNearestColumn(segment.Bounds.CenterX, columnCenters);
                if (nearest < 0)
                {
                    continue;
                }

                var cellBounds = new DocumentBounds(
                    Math.Min(segment.Bounds.Left, tableColumns[nearest].Left),
                    Math.Min(segment.Bounds.Top, tableTop),
                    Math.Max(segment.Bounds.Right, tableColumns[nearest].Right),
                    Math.Max(segment.Bounds.Bottom, tableTop));
                headerCells.Add(new DocumentTableCell(
                    headerRow,
                    nearest,
                    1,
                    1,
                    segment.Text,
                    DocumentCellValueClassifier.Classify(segment.Text),
                    Math.Max(segment.Confidence, firstTable.Confidence),
                    cellBounds));
            }
        }

        if (headerCells.Count == 0)
        {
            return tables;
        }

        var bodyCells = firstTable.Cells
            .Select(cell => cell with { RowIndex = cell.RowIndex + headerCandidateRows.Length })
            .ToList();
        bodyCells.AddRange(headerCells);
        var combinedBounds = DocumentBounds.Union(
            bodyCells.Select(static cell => cell.Bounds));
        var headerTable = new DocumentTable(
            firstTable.PageNumber,
            firstTable.TableIndex,
            newRowCount,
            firstTable.ColumnCount,
            combinedBounds,
            firstTable.SourceKind,
            firstTable.Confidence,
            bodyCells,
            firstTable.Warnings);

        var result = new List<DocumentTable>(tables.Count) { headerTable };
        result.AddRange(tables.Skip(1));
        return result;
    }

    private static int FindNearestColumn(double position, double[] centers)
    {
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < centers.Length; index++)
        {
            var distance = Math.Abs(centers[index] - position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestDistance <= Math.Max(4, centers.Length > 1 ? (centers[1] - centers[0]) * 0.45 : 6)
            ? bestIndex
            : -1;
    }

    private static List<DocumentTable> BuildTables(
        int pageNumber,
        DocumentTableSourceKind sourceKind,
        IReadOnlyList<SegmentedRow> rows,
        IReadOnlyList<ColumnCluster> columns,
        double medianHeight)
    {
        if (columns.Count is < 2 or > MaximumColumns)
        {
            return [];
        }

        var mappedRows = rows
            .Select(row => MapRow(row, columns, medianHeight))
            .ToArray();
        // Excel-exported forms often contain tall merged rows. Their text
        // baselines are far apart even though the columns remain stable;
        // preserve that grid instead of degrading the page to a single
        // fallback column.
        var sparseGrid = columns.Count >= 3 && columns.Count <= 8;
        var maximumRowGap = sparseGrid
            ? medianHeight * 12
            : medianHeight * 3.5;
        var candidateRows = mappedRows
            .Select((row, index) => new IndexedMappedRow(index, row))
            .Where(static value => value.Row.Cells.Count >= 2)
            .ToArray();
        if (candidateRows.Length < 2)
        {
            return [];
        }

        var groups = new List<List<IndexedMappedRow>>();
        foreach (var candidate in candidateRows)
        {
            if (groups.Count == 0)
            {
                groups.Add([candidate]);
                continue;
            }

            var current = groups[^1];
            var previous = current[^1];
            var gap = candidate.Row.Bounds.Top - previous.Row.Bounds.Bottom;
            var columnsOverlap = ColumnOverlap(previous.Row.Cells.Keys, candidate.Row.Cells.Keys);
            if (gap > maximumRowGap || columnsOverlap < 0.34)
            {
                groups.Add([candidate]);
            }
            else
            {
                current.Add(candidate);
            }
        }

        var result = new List<DocumentTable>();
        foreach (var group in groups.Where(static group => group.Count >= 2))
        {
            var usedColumns = group
                .SelectMany(static item => item.Row.Cells.Keys)
                .Distinct()
                .Order()
                .ToArray();
            if (usedColumns.Length < 2)
            {
                continue;
            }

            var columnMap = usedColumns
                .Select((column, index) => (column, index))
                .ToDictionary(static value => value.column, static value => value.index);
            var cells = new List<DocumentTableCell>();
            for (var rowIndex = 0; rowIndex < group.Count; rowIndex++)
            {
                foreach (var entry in group[rowIndex].Row.Cells.OrderBy(static entry => entry.Key))
                {
                    var text = JoinSegmentText(entry.Value);
                    var confidence = entry.Value.Average(static segment => segment.Confidence);
                    cells.Add(new DocumentTableCell(
                        rowIndex,
                        columnMap[entry.Key],
                        1,
                        1,
                        text,
                        DocumentCellValueClassifier.Classify(text),
                        confidence,
                        DocumentBounds.Union(entry.Value.Select(static segment => segment.Bounds))));
                }
            }

            var occupiedCells = group.Sum(static row => row.Row.Cells.Count);
            var consistency = (double)occupiedCells / (group.Count * usedColumns.Length);
            var confidenceScore = Math.Clamp(
                cells.Average(static cell => cell.Confidence) * (0.65 + (0.35 * consistency)),
                0,
                1);
            var warnings = new List<string>();
            if (sourceKind == DocumentTableSourceKind.OpticalCharacterRecognition)
            {
                warnings.Add("OCR_CONTENT_REQUIRES_REVIEW");
            }

            if (cells.Any(static cell => cell.Confidence < 0.75))
            {
                warnings.Add("LOW_CONFIDENCE_CELLS");
            }

            if (consistency < 0.6)
            {
                warnings.Add("SPARSE_OR_IRREGULAR_TABLE");
            }

            result.Add(new DocumentTable(
                pageNumber,
                result.Count + 1,
                group.Count,
                usedColumns.Length,
                DocumentBounds.Union(group.Select(static row => row.Row.Bounds)),
                sourceKind,
                confidenceScore,
                cells,
                warnings));
        }

        return result;
    }

    private static MappedRow MapRow(
        SegmentedRow row,
        IReadOnlyList<ColumnCluster> columns,
        double medianHeight)
    {
        var cells = new Dictionary<int, List<TextSegment>>();
        foreach (var segment in row.Segments)
        {
            var nearest = columns
                .Select((column, index) => (index, distance: Math.Abs(column.Position - segment.Bounds.CenterX)))
                .MinBy(static value => value.distance);
            var tolerance = Math.Max(medianHeight * 2.4, segment.Bounds.Height * 2);
            if (nearest.distance > tolerance)
            {
                continue;
            }

            if (!cells.TryGetValue(nearest.index, out var values))
            {
                values = [];
                cells[nearest.index] = values;
            }

            values.Add(segment);
        }

        return new MappedRow(row.Bounds, cells);
    }

    private static ColumnCluster[] BuildColumnClusters(
        IReadOnlyList<SegmentedRow> rows,
        double medianHeight,
        double pageWidth)
    {
        var starts = rows
            .Where(static row => row.Segments.Count >= 2)
            .SelectMany(static row => row.Segments)
            .Select(static segment => segment.Bounds.CenterX)
            .Order()
            .ToArray();
        if (starts.Length < 4)
        {
            return [];
        }

        var tolerance = Math.Max(medianHeight * 2, pageWidth * 0.012);
        var clusters = new List<List<double>>();
        foreach (var start in starts)
        {
            if (clusters.Count == 0 || Math.Abs(clusters[^1].Average() - start) > tolerance)
            {
                clusters.Add([start]);
            }
            else
            {
                clusters[^1].Add(start);
            }
        }

        var minimumVotes = Math.Max(2, (int)Math.Ceiling(rows.Count * 0.2));
        return clusters
            .Where(cluster => cluster.Count >= minimumVotes)
            .Select(static cluster => new ColumnCluster(cluster.Average(), cluster.Count))
            .Take(MaximumColumns + 1)
            .ToArray();
    }

    private static TextRow[] BuildRows(
        IReadOnlyList<DocumentWord> words,
        double medianHeight)
    {
        var rows = new List<TextRow>();
        foreach (var word in words.OrderBy(static word => word.Bounds.CenterY).ThenBy(static word => word.Bounds.Left))
        {
            TextRow? best = null;
            var bestDistance = double.MaxValue;
            for (var index = Math.Max(0, rows.Count - 6); index < rows.Count; index++)
            {
                var row = rows[index];
                var centerDistance = Math.Abs(row.Bounds.CenterY - word.Bounds.CenterY);
                var overlap = VerticalOverlap(row.Bounds, word.Bounds);
                var minimumHeight = Math.Min(row.Bounds.Height, word.Bounds.Height);
                if (overlap < minimumHeight * 0.35 && centerDistance > medianHeight * 0.65)
                {
                    continue;
                }

                if (centerDistance < bestDistance)
                {
                    best = row;
                    bestDistance = centerDistance;
                }
            }

            if (best is null)
            {
                rows.Add(new TextRow([word]));
            }
            else
            {
                best.Add(word);
            }
        }

        return rows.OrderBy(static row => row.Bounds.Top).ToArray();
    }

    private static TextSegment[] BuildSegments(TextRow row, double medianHeight)
    {
        var words = row.Words.OrderBy(static word => word.Bounds.Left).ToArray();
        if (words.Length == 0)
        {
            return [];
        }

        var characterWidths = words
            .Where(static word => word.Text.Length > 0)
            .Select(static word => word.Bounds.Width / word.Text.Length)
            .Where(static value => value > 0)
            .ToArray();
        var medianCharacterWidth = characterWidths.Length == 0 ? medianHeight * 0.5 : Median(characterWidths);
        var gapThreshold = Math.Max(medianHeight * 0.9, medianCharacterWidth * 1.8);
        var groups = new List<List<DocumentWord>> { new() { words[0] } };
        for (var index = 1; index < words.Length; index++)
        {
            var gap = words[index].Bounds.Left - words[index - 1].Bounds.Right;
            if (gap > gapThreshold)
            {
                groups.Add([words[index]]);
            }
            else
            {
                groups[^1].Add(words[index]);
            }
        }

        return groups.Select(CreateSegment).ToArray();
    }

    private static TextSegment CreateSegment(IReadOnlyList<DocumentWord> words) =>
        new(
            JoinWords(words),
            DocumentBounds.Union(words.Select(static word => word.Bounds)),
            words.Average(static word => word.Confidence));

    /// <summary>
    /// Detects the recurring "label column" pattern of a form page (for
    /// example "申请方名称:" at x=36 with its value at x=109). When at least
    /// four rows agree on the label/value boundary, returns a single
    /// two-column table covering exactly those rows in reading order. This
    /// runs before generic table clustering so form pages are not broken into
    /// fragments by column voting.
    /// </summary>
    private static DocumentTable? TryCreateLabelValueTable(
        int pageNumber,
        DocumentTableSourceKind sourceKind,
        IReadOnlyList<TextRow> rows,
        out IReadOnlyList<TextRow> remainingRows)
    {
        remainingRows = rows;
        var boundary = DetectLabelValueBoundary(rows, out var labelValueRows);
        if (boundary is null)
        {
            return null;
        }

        var matched = labelValueRows.ToHashSet(ReferenceEqualityComparer.Instance);
        remainingRows = rows.Where(row => !matched.Contains(row)).ToArray();

        var cells = new List<DocumentTableCell>(labelValueRows.Count * 2);
        for (var index = 0; index < labelValueRows.Count; index++)
        {
            var row = labelValueRows[index];
            var (label, value) = SplitRowAtBoundary(row, boundary.Value);
            if (label is not null)
            {
                cells.Add(new DocumentTableCell(
                    index,
                    0,
                    1,
                    1,
                    label.Text,
                    DocumentCellValueClassifier.Classify(label.Text),
                    label.Confidence,
                    label.Bounds));
            }

            if (value is not null)
            {
                cells.Add(new DocumentTableCell(
                    index,
                    1,
                    1,
                    1,
                    value.Text,
                    DocumentCellValueClassifier.Classify(value.Text),
                    value.Confidence,
                    value.Bounds));
            }
        }

        var warnings = new List<string>();
        if (sourceKind == DocumentTableSourceKind.OpticalCharacterRecognition)
        {
            warnings.Add("OCR_CONTENT_REQUIRES_REVIEW");
        }

        return new DocumentTable(
            pageNumber,
            1,
            labelValueRows.Count,
            2,
            DocumentBounds.Union(cells.Select(static cell => cell.Bounds)),
            sourceKind,
            Math.Min(0.9, cells.Average(static cell => cell.Confidence)),
            cells,
            warnings);
    }

    private static DocumentTable? CreateFallbackTable(
        int pageNumber,
        DocumentTableSourceKind sourceKind,
        IReadOnlyList<SegmentedRow> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var cells = rows
            .Select((row, index) =>
            {
                var text = JoinSegmentText(row.Segments);
                return new DocumentTableCell(
                    index,
                    0,
                    1,
                    1,
                    text,
                    DocumentCellValueClassifier.Classify(text),
                    row.Segments.Average(static segment => segment.Confidence),
                    row.Bounds);
            })
            .ToArray();
        var warnings = new List<string> { "TABLE_STRUCTURE_UNCERTAIN" };
        if (sourceKind == DocumentTableSourceKind.OpticalCharacterRecognition)
        {
            warnings.Add("OCR_CONTENT_REQUIRES_REVIEW");
        }

        return new DocumentTable(
            pageNumber,
            1,
            rows.Count,
            1,
            DocumentBounds.Union(rows.Select(static row => row.Bounds)),
            sourceKind,
            Math.Min(0.49, cells.Average(static cell => cell.Confidence) * 0.55),
            cells,
            warnings);
    }

    private static string JoinWords(IReadOnlyList<DocumentWord> words)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < words.Count; index++)
        {
            if (index > 0 && NeedsSpace(words[index - 1].Text, words[index].Text))
            {
                builder.Append(' ');
            }

            builder.Append(words[index].Text);
        }

        return builder.ToString();
    }

    private static string JoinSegmentText(IEnumerable<TextSegment> segments) =>
        string.Join(' ', segments.OrderBy(static segment => segment.Bounds.Left).Select(static segment => segment.Text));

    /// <summary>
    /// Looks for a recurring "label column" edge across the fallback rows:
    /// rows whose first segment is short (for example "申请方名称:") followed
    /// by one or more value segments starting at a consistent left position.
    /// Returns the median boundary x when at least four rows agree within a
    /// half-character tolerance.
    /// </summary>
    private static double? DetectLabelValueBoundary(
        IReadOnlyList<TextRow> rows,
        out List<TextRow> matchingRows)
    {
        matchingRows = [];
        var boundaries = new List<double>();
        var candidates = new List<(TextRow Row, double Boundary)>();
        foreach (var row in rows)
        {
            var words = row.Words.OrderBy(static word => word.Bounds.Left).ToArray();
            if (words.Length < 2)
            {
                continue;
            }

            var first = words[0];
            var second = words[1];
            var firstEnd = first.Bounds.Right;
            var gap = second.Bounds.Left - firstEnd;
            if (first.Text.Length <= 16 && gap >= 4)
            {
                boundaries.Add(firstEnd);
                candidates.Add((row, firstEnd));
            }
        }

        if (candidates.Count < 4)
        {
            return null;
        }

        var reference = Median(boundaries);
        var tolerance = Math.Max(20, reference * 0.06);
        var agreeing = candidates
            .Where(candidate => Math.Abs(candidate.Boundary - reference) <= tolerance)
            .ToArray();
        if (agreeing.Length < 4 || agreeing.Length * 2 < candidates.Count)
        {
            return null;
        }

        // For the agreeing rows, values start just past the label column. Any
        // row whose second word starts within one character width of the
        // reference boundary is considered a label row even if its own label
        // end differs slightly (for example a longer label like
        // "据称样品名称:"), as long as the value column is aligned.
        var valueReference = agreeing
            .Select(candidate => candidate.Row.Words
                .OrderBy(static word => word.Bounds.Left)
                .Skip(1)
                .FirstOrDefault())
            .Where(static word => word is not null)
            .Select(static word => word!.Bounds.Left)
            .ToArray();
        var valueStart = valueReference.Length == 0 ? reference : Median(valueReference);

        // Label/value rows must form one contiguous vertical band; a large
        // gap (for example two unrelated two-column blocks on the same page)
        // must not be merged into a single form table.
        var ordered = agreeing
            .Select(static candidate => candidate.Row)
            .OrderBy(static row => row.Bounds.Top)
            .ToArray();
        var band = new List<TextRow>();
        var bestBand = new List<TextRow>();
        foreach (var row in ordered)
        {
            if (band.Count > 0)
            {
                var previous = band[^1];
                var gap = row.Bounds.Top - previous.Bounds.Bottom;
                if (gap > Math.Max(previous.Bounds.Height, row.Bounds.Height) * 1.2)
                {
                    if (band.Count > bestBand.Count)
                    {
                        bestBand = band;
                    }

                    band = [];
                }
            }

            band.Add(row);
        }

        if (band.Count > bestBand.Count)
        {
            bestBand = band;
        }

        if (bestBand.Count < 4)
        {
            return null;
        }

        matchingRows.AddRange(bestBand);
        foreach (var candidate in candidates)
        {
            if (matchingRows.Contains(candidate.Row))
            {
                continue;
            }

            var second = candidate.Row.Words
                .OrderBy(static word => word.Bounds.Left)
                .Skip(1)
                .FirstOrDefault();
            if (second is not null &&
                Math.Abs(second.Bounds.Left - valueStart) <= Math.Max(4, valueStart * 0.03))
            {
                matchingRows.Add(candidate.Row);
            }
        }

        return reference;
    }

    private static (TextSegment? Label, TextSegment? Value) SplitRowAtBoundary(
        TextRow row,
        double boundary)
    {
        TextSegment? label = null;
        TextSegment? value = null;
        var boundaryTolerance = Math.Max(10, boundary * 0.05);
        foreach (var word in row.Words.OrderBy(static word => word.Bounds.Left))
        {
            var segment = CreateSegment([word]);
            if (segment.Bounds.Right <= boundary + boundaryTolerance)
            {
                label = label is null
                    ? segment
                    : new TextSegment(
                        label.Text + segment.Text,
                        DocumentBounds.Union([label.Bounds, segment.Bounds]),
                        Math.Min(label.Confidence, segment.Confidence));
                continue;
            }

            value = value is null
                ? segment
                : new TextSegment(
                    value.Text + " " + segment.Text,
                    DocumentBounds.Union([value.Bounds, segment.Bounds]),
                    Math.Min(value.Confidence, segment.Confidence));
        }

        return (label, value);
    }

    private static bool NeedsSpace(string left, string right) =>
        left.Length > 0 &&
        right.Length > 0 &&
        IsAsciiWordCharacter(left[^1]) &&
        IsAsciiWordCharacter(right[0]);

    private static bool IsAsciiWordCharacter(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static double ColumnOverlap(IEnumerable<int> left, IEnumerable<int> right)
    {
        var leftSet = left.ToHashSet();
        var rightSet = right.ToHashSet();
        var union = leftSet.Union(rightSet).Count();
        return union == 0 ? 0 : (double)leftSet.Intersect(rightSet).Count() / union;
    }

    private static double VerticalOverlap(DocumentBounds left, DocumentBounds right) =>
        Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(static value => double.IsFinite(value) && value > 0).Order().ToArray();
        if (ordered.Length == 0)
        {
            return 1;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class TextRow
    {
        private readonly List<DocumentWord> _words;

        public TextRow(List<DocumentWord> words)
        {
            _words = words;
            Bounds = DocumentBounds.Union(words.Select(static word => word.Bounds));
        }

        public IReadOnlyList<DocumentWord> Words => _words;

        public DocumentBounds Bounds { get; private set; }

        public void Add(DocumentWord word)
        {
            _words.Add(word);
            Bounds = DocumentBounds.Union(_words.Select(static value => value.Bounds));
        }
    }

    private sealed record TextSegment(string Text, DocumentBounds Bounds, double Confidence);

    private sealed record SegmentedRow(TextRow Row, IReadOnlyList<TextSegment> Segments)
    {
        public DocumentBounds Bounds => Row.Bounds;
    }

    private sealed record ColumnCluster(double Position, int Votes);

    private sealed record MappedRow(
        DocumentBounds Bounds,
        IReadOnlyDictionary<int, List<TextSegment>> Cells);

    private sealed record IndexedMappedRow(int Index, MappedRow Row);
}
