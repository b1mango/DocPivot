namespace DocPivot.Core.Jobs;

public readonly record struct JobProgress
{
    private JobProgress(string stage, int current, int total)
    {
        Stage = stage;
        Current = current;
        Total = total;
    }

    public string Stage { get; }

    public int Current { get; }

    public int Total { get; }

    public double Fraction => Total == 0 ? 0 : (double)Current / Total;

    public static JobProgress Create(string stage, int current, int total)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegative(total);

        if (current > total)
        {
            throw new ArgumentOutOfRangeException(nameof(current), "Current progress cannot exceed total progress.");
        }

        return new JobProgress(stage, current, total);
    }
}

