namespace DocPivot.Core.Tables;

internal static class RuledTableDetector
{
    private const double CoordinateTolerance = 1.5;
    private const double MinimumLineLength = 8;
    private const double MinimumBorderCoverage = 0.85;
    private const int MaximumGridCells = 100_000;
    private const int MaximumCandidateCoordinates = 400;
    // Double and thick scanned borders yield parallel rules a few points apart; no real cell is that thin.
    private const double MinimumCellSize = 4;

    public static IReadOnlyList<DocumentTable> Detect(
        int pageNumber,
        DocumentTableSourceKind sourceKind,
        IReadOnlyList<DocumentWord> words,
        IReadOnlyList<DocumentLine> lines)
    {
        var axes = lines.Select(ToAxisLine).OfType<AxisLine>()
            .OrderBy(static line => line.Left).ToArray();
        var roots = Enumerable.Range(0, axes.Length).ToArray();
        for (var left = 0; left < axes.Length; left++)
        {
            for (var right = left + 1; right < axes.Length &&
                axes[right].Left <= axes[left].Right + CoordinateTolerance; right++)
            {
                if (Touches(axes[left], axes[right]))
                {
                    Union(roots, left, right);
                }
            }
        }

        var tables = new List<DocumentTable>();
        foreach (var component in Enumerable.Range(0, axes.Length).GroupBy(index => Find(roots, index)))
        {
            var componentLines = component.Select(index => axes[index]).ToArray();
            var table = BuildTable(pageNumber, sourceKind, words, componentLines);
            if (table is not null)
            {
                tables.Add(table with { TableIndex = tables.Count + 1 });
            }
        }

        return tables;
    }

    private static DocumentTable? BuildTable(
        int pageNumber,
        DocumentTableSourceKind sourceKind,
        IReadOnlyList<DocumentWord> words,
        AxisLine[] lines)
    {
        var horizontal = lines.Where(static line => line.Horizontal).ToArray();
        var vertical = lines.Where(static line => !line.Horizontal).ToArray();
        var candidateXs = Coordinates(vertical.Select(static line => line.Position));
        var candidateYs = Coordinates(horizontal.Select(static line => line.Position));
        if (candidateXs.Length < 3 || candidateYs.Length < 3 ||
            candidateXs.Length > MaximumCandidateCoordinates || candidateYs.Length > MaximumCandidateCoordinates)
        {
            return null;
        }

        if (TrimGrid(vertical, horizontal, candidateXs, candidateYs) is not var (xs, ys) ||
            xs.Length > 65 || (long)(xs.Length - 1) * (ys.Length - 1) > MaximumGridCells)
        {
            return null;
        }

        var rowCount = ys.Length - 1;
        var columnCount = xs.Length - 1;
        var roots = Enumerable.Range(0, rowCount * columnCount).ToArray();
        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < columnCount; column++)
            {
                var index = row * columnCount + column;
                if (column + 1 < columnCount && !Covered(vertical, xs[column + 1], ys[row], ys[row + 1]))
                {
                    Union(roots, index, index + 1);
                }

                if (row + 1 < rowCount && !Covered(horizontal, ys[row + 1], xs[column], xs[column + 1]))
                {
                    Union(roots, index, index + columnCount);
                }
            }
        }

        var cells = new List<DocumentTableCell>();
        foreach (var group in Enumerable.Range(0, roots.Length).GroupBy(index => Find(roots, index)))
        {
            var slots = group.ToArray();
            var top = slots.Min(index => index / columnCount);
            var bottom = slots.Max(index => index / columnCount) + 1;
            var left = slots.Min(index => index % columnCount);
            var right = slots.Max(index => index % columnCount) + 1;
            // Missing or damaged borders must not create a nonrectangular Excel merge.
            if ((bottom - top) * (right - left) != slots.Length)
            {
                return null;
            }

            var bounds = new DocumentBounds(xs[left], ys[top], xs[right], ys[bottom]);
            var cellWords = words.Where(word =>
                word.Bounds.CenterX >= bounds.Left && word.Bounds.CenterX < bounds.Right &&
                word.Bounds.CenterY >= bounds.Top && word.Bounds.CenterY < bounds.Bottom).ToArray();
            var text = JoinCellWords(cellWords);
            cells.Add(new DocumentTableCell(
                top, left, bottom - top, right - left, text,
                DocumentCellValueClassifier.Classify(text),
                cellWords.Length > 0 ? cellWords.Average(static word => word.Confidence) : 1,
                bounds));
        }

        var populated = cells.Where(static cell => cell.Text.Length > 0).ToArray();
        if (populated.Length < 2)
        {
            return null;
        }

        var warnings = new List<string>();
        if (sourceKind == DocumentTableSourceKind.OpticalCharacterRecognition)
        {
            warnings.Add("OCR_CONTENT_REQUIRES_REVIEW");
        }

        if (populated.Any(static cell => cell.Confidence < 0.75))
        {
            warnings.Add("LOW_CONFIDENCE_CELLS");
        }

        return new DocumentTable(
            pageNumber, 1, rowCount, columnCount,
            new DocumentBounds(xs[0], ys[0], xs[^1], ys[^1]),
            sourceKind, populated.Average(static cell => cell.Confidence), cells, warnings);
    }

    private static string JoinCellWords(DocumentWord[] words)
    {
        var rows = new List<List<DocumentWord>>();
        foreach (var word in words.OrderBy(static word => word.Bounds.CenterY).ThenBy(static word => word.Bounds.Left))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(row[0].Bounds.CenterY - word.Bounds.CenterY) >
                Math.Min(row[0].Bounds.Height, word.Bounds.Height) * 0.6)
            {
                rows.Add([word]);
            }
            else
            {
                row.Add(word);
            }
        }

        return string.Join('\n', rows.Select(static row =>
        {
            var ordered = row.OrderBy(static word => word.Bounds.Left).ToArray();
            var builder = new System.Text.StringBuilder();
            for (var index = 0; index < ordered.Length; index++)
            {
                if (index > 0 && char.IsAsciiLetterOrDigit(ordered[index - 1].Text[^1]) &&
                    char.IsAsciiLetterOrDigit(ordered[index].Text[0]))
                {
                    builder.Append(' ');
                }

                builder.Append(ordered[index].Text);
            }

            return builder.ToString();
        }));
    }

    /// <summary>
    /// Scans add strokes that touch the grid: handwriting, seal borders, signature underlines.
    /// The frame first shrinks past edges that are not drawn. Interior rules are then added one at
    /// a time, longest first, and only when they span a whole cell edge of the grid accepted so
    /// far, so strokes cannot vouch for each other through the tiny cells they would create.
    /// </summary>
    private static (double[] Xs, double[] Ys)? TrimGrid(
        AxisLine[] vertical,
        AxisLine[] horizontal,
        double[] candidateXs,
        double[] candidateYs)
    {
        var xs = candidateXs.ToList();
        var ys = candidateYs.ToList();
        while (true)
        {
            if (xs.Count < 2 || ys.Count < 2)
            {
                return null;
            }

            var edges = new (List<double> Axis, int Index, double Missing)[]
            {
                (xs, 0, MissingRatio(vertical, xs[0], ys[0], ys[^1])),
                (xs, xs.Count - 1, MissingRatio(vertical, xs[^1], ys[0], ys[^1])),
                (ys, 0, MissingRatio(horizontal, ys[0], xs[0], xs[^1])),
                (ys, ys.Count - 1, MissingRatio(horizontal, ys[^1], xs[0], xs[^1])),
            };
            var worst = edges.MaxBy(static edge => edge.Missing);
            if (worst.Missing == 0)
            {
                break;
            }

            worst.Axis.RemoveAt(worst.Index);
        }

        var (left, right, top, bottom) = (xs[0], xs[^1], ys[0], ys[^1]);
        var keptXs = new List<double> { left, right };
        var keptYs = new List<double> { top, bottom };
        var pendingXs = xs.Skip(1).SkipLast(1).ToList();
        var pendingYs = ys.Skip(1).SkipLast(1).ToList();
        while (true)
        {
            var best = (Vertical: false, Position: 0.0, Length: 0.0);
            foreach (var x in pendingXs)
            {
                var length = (bottom - top) - Missing(vertical, x, top, bottom);
                if (length > best.Length && !NearKept(keptXs, x) && SpansCellEdge(vertical, x, keptYs))
                {
                    best = (true, x, length);
                }
            }

            foreach (var y in pendingYs)
            {
                var length = (right - left) - Missing(horizontal, y, left, right);
                if (length > best.Length && !NearKept(keptYs, y) && SpansCellEdge(horizontal, y, keptXs))
                {
                    best = (false, y, length);
                }
            }

            if (best.Length == 0)
            {
                break;
            }

            var (kept, pending) = best.Vertical ? (keptXs, pendingXs) : (keptYs, pendingYs);
            pending.Remove(best.Position);
            var index = kept.BinarySearch(best.Position);
            kept.Insert(index < 0 ? ~index : index, best.Position);
        }

        return keptXs.Count >= 3 && keptYs.Count >= 3 ? (keptXs.ToArray(), keptYs.ToArray()) : null;
    }

    private static bool NearKept(List<double> kept, double position) =>
        kept.Exists(value => Math.Abs(value - position) < MinimumCellSize);

    private static bool SpansCellEdge(AxisLine[] lines, double position, List<double> crossing)
    {
        for (var index = 0; index + 1 < crossing.Count; index++)
        {
            if (Covered(lines, position, crossing[index], crossing[index + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Uncovered share of an edge beyond the allowed gap; zero when the edge counts as drawn.</summary>
    private static double MissingRatio(AxisLine[] lines, double position, double start, double end)
    {
        var length = end - start;
        var missing = Missing(lines, position, start, end);
        return missing <= Math.Max(CoordinateTolerance, length * (1 - MinimumBorderCoverage))
            ? 0
            : missing / Math.Max(length, CoordinateTolerance);
    }

    private static bool Covered(AxisLine[] lines, double position, double start, double end) =>
        MissingRatio(lines, position, start, end) == 0;

    private static double Missing(AxisLine[] lines, double position, double start, double end)
    {
        var covered = start;
        var missing = 0.0;
        foreach (var line in lines.Where(line => Math.Abs(line.Position - position) <= CoordinateTolerance)
            .OrderBy(static line => line.Start))
        {
            if (line.End <= covered || line.Start >= end)
            {
                continue;
            }

            missing += Math.Max(0, line.Start - covered);
            covered = Math.Min(end, line.End);
        }

        missing += end - covered;
        return missing;
    }

    private static double[] Coordinates(IEnumerable<double> values)
    {
        var clusters = new List<List<double>>();
        foreach (var value in values.Order())
        {
            if (clusters.Count == 0 || value - clusters[^1][0] > CoordinateTolerance)
            {
                clusters.Add([value]);
            }
            else
            {
                clusters[^1].Add(value);
            }
        }

        return clusters.Select(static cluster => cluster.Average()).ToArray();
    }

    private static AxisLine? ToAxisLine(DocumentLine line)
    {
        if (!double.IsFinite(line.StartX) || !double.IsFinite(line.StartY) ||
            !double.IsFinite(line.EndX) || !double.IsFinite(line.EndY))
        {
            return null;
        }

        if (Math.Abs(line.EndY - line.StartY) <= CoordinateTolerance &&
            Math.Abs(line.EndX - line.StartX) >= MinimumLineLength)
        {
            return new AxisLine(true, (line.StartY + line.EndY) / 2,
                Math.Min(line.StartX, line.EndX), Math.Max(line.StartX, line.EndX));
        }

        return Math.Abs(line.EndX - line.StartX) <= CoordinateTolerance &&
            Math.Abs(line.EndY - line.StartY) >= MinimumLineLength
                ? new AxisLine(false, (line.StartX + line.EndX) / 2,
                    Math.Min(line.StartY, line.EndY), Math.Max(line.StartY, line.EndY))
                : null;
    }

    private static bool Touches(AxisLine left, AxisLine right) =>
        left.Left <= right.Right + CoordinateTolerance && right.Left <= left.Right + CoordinateTolerance &&
        left.Top <= right.Bottom + CoordinateTolerance && right.Top <= left.Bottom + CoordinateTolerance;

    private static int Find(int[] roots, int index)
    {
        while (roots[index] != index)
        {
            roots[index] = roots[roots[index]];
            index = roots[index];
        }

        return index;
    }

    private static void Union(int[] roots, int left, int right) => roots[Find(roots, right)] = Find(roots, left);

    private sealed record AxisLine(bool Horizontal, double Position, double Start, double End)
    {
        public double Left => Horizontal ? Start : Position;
        public double Right => Horizontal ? End : Position;
        public double Top => Horizontal ? Position : Start;
        public double Bottom => Horizontal ? Position : End;
    }
}
