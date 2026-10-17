namespace CounterShift.Storage;

/// <summary>Which backend the facade uses (section "CounterShift:Storage"). M6 replaces this with read/write modes.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "CounterShift:Storage";

    /// <summary>The <see cref="ICounterBackend.Name"/> to read from and write to: "postgres" or "redis".</summary>
    public string Backend { get; set; } = "postgres";
}
