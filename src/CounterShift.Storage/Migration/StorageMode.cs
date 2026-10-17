namespace CounterShift.Storage.Migration;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "'Single' is the migration vocabulary from Grab's post and spec §7, not the float type.")]
public enum WriteKind
{
    /// <summary>Write to one backend.</summary>
    Single,

    /// <summary>Write to the primary (must succeed), then the secondary (failure tolerated and counted).</summary>
    Dual,
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "'Single' is the migration vocabulary from Grab's post and spec §7, not the float type.")]
public enum ReadKind
{
    /// <summary>Read from one backend.</summary>
    Single,

    /// <summary>Serve from the primary; for a sample of counters, also read the secondary in the background and compare.</summary>
    Shadow,

    /// <summary>Serve a deterministic share of counters from the secondary, the rest from the primary.</summary>
    Split,
}

public sealed record WriteMode(WriteKind Kind, string Primary, string? Secondary = null)
{
    public override string ToString() => Kind == WriteKind.Single ? $"Single({Primary})" : $"Dual({Primary}→{Secondary})";
}

public sealed record ReadMode(ReadKind Kind, string Primary, string? Secondary = null, double Percent = 0)
{
    public override string ToString() => Kind switch
    {
        ReadKind.Single => $"Single({Primary})",
        _ => $"{Kind}({Primary}, {Secondary}, {Percent}%)",
    };
}

/// <summary>
/// The two independent knobs from spec §7.1. Immutable: a reload swaps the whole object, so a request
/// never sees half of an old mode and half of a new one.
/// </summary>
public sealed record StorageMode(WriteMode Write, ReadMode Read)
{
    public static StorageMode SinglePostgres { get; } = new(new(WriteKind.Single, "postgres"), new(ReadKind.Single, "postgres"));

    /// <summary>Backends that receive every write.</summary>
    public IReadOnlySet<string> WrittenBackends =>
        Write.Secondary is null ? new HashSet<string> { Write.Primary } : new HashSet<string> { Write.Primary, Write.Secondary };

    /// <summary>
    /// Backends whose failure makes requests fail, so readiness must check them. A dual-write secondary and a
    /// shadow are deliberately excluded: their failures are tolerated and counted, never surfaced to callers.
    /// </summary>
    public IReadOnlySet<string> RequiredBackends
    {
        get
        {
            var required = new HashSet<string> { Write.Primary, Read.Primary };
            if (Read.Kind == ReadKind.Split && Read.Percent > 0 && Read.Secondary is not null)
            {
                required.Add(Read.Secondary);
            }
            return required;
        }
    }

    /// <summary>Every rule that makes a mode unsafe. An empty list means the mode may be applied.</summary>
    public IReadOnlyList<string> Validate(IReadOnlySet<string> registeredBackends)
    {
        ArgumentNullException.ThrowIfNull(registeredBackends);
        var errors = new List<string>();

        void RequireRegistered(string? name, string role)
        {
            if (name is not null && !registeredBackends.Contains(name))
            {
                errors.Add($"{role} backend '{name}' is not registered (available: {string.Join(", ", registeredBackends)})");
            }
        }

        RequireRegistered(Write.Primary, "write primary");
        RequireRegistered(Write.Secondary, "write secondary");
        RequireRegistered(Read.Primary, "read primary");
        RequireRegistered(Read.Secondary, "read secondary");

        switch (Write.Kind)
        {
            case WriteKind.Single when Write.Secondary is not null:
                errors.Add("Single write mode must not name a secondary");
                break;
            case WriteKind.Dual when Write.Secondary is null || Write.Secondary == Write.Primary:
                errors.Add("Dual write mode needs a secondary that differs from the primary");
                break;
        }

        switch (Read.Kind)
        {
            case ReadKind.Single when Read.Secondary is not null:
                errors.Add("Single read mode must not name a secondary");
                break;
            case ReadKind.Shadow or ReadKind.Split when Read.Secondary is null || Read.Secondary == Read.Primary:
                errors.Add($"{Read.Kind} read mode needs a secondary that differs from the primary");
                break;
        }
        if (Read.Percent is < 0 or > 100 || double.IsNaN(Read.Percent))
        {
            errors.Add($"read percent must be within [0, 100], got {Read.Percent}");
        }

        // The safety rule behind the whole migration (the NaiveSwitchTests lesson): never read from a store that
        // isn't receiving every write, or it will silently answer from incomplete data.
        foreach (var read in new[] { Read.Primary, Read.Secondary }.OfType<string>())
        {
            if (!WrittenBackends.Contains(read))
            {
                errors.Add($"backend '{read}' is read from but not written to; enable Dual writes to it first");
            }
        }
        return errors;
    }

    public override string ToString() => $"write={Write} read={Read}";
}
