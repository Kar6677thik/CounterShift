namespace CounterShift.Domain;

public enum ValidationErrorKind
{
    /// <summary>The request itself is wrong (unknown type, missing field). Maps to HTTP 400.</summary>
    Malformed,

    /// <summary>The request is well-formed but can't be accepted at this time (too late, from the future). Maps to HTTP 422.</summary>
    Unprocessable,
}

public sealed record ValidationError(string Field, string Message, ValidationErrorKind Kind = ValidationErrorKind.Malformed);

/// <summary>Thrown when caller input violates a domain rule. The API layer turns it into a problem+json response.</summary>
public sealed class DomainValidationException : Exception
{
    public DomainValidationException(IReadOnlyList<ValidationError> errors)
        : base(string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}")))
    {
        Errors = errors;
    }

    public DomainValidationException(string field, string message, ValidationErrorKind kind = ValidationErrorKind.Malformed)
        : this([new ValidationError(field, message, kind)])
    {
    }

    public IReadOnlyList<ValidationError> Errors { get; }
}
