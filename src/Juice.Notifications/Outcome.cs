namespace Juice.Notifications
{
    /// <summary>
    /// How a command ended, for the API to turn into a status code. Errors are short machine codes with a sentence for people.
    /// <see cref="Invalid"/> is a malformed command (400); <see cref="Unprocessable"/> is a well-formed one the rules refuse, such as data that
    /// fails its type's schema (422); <see cref="Forbidden"/> is a caller acting outside what it owns (403).
    /// </summary>
    public enum OutcomeKind { Ok, Created, NotFound, Conflict, Invalid, Unprocessable, Forbidden }

    /// <summary>One problem with one field: a JSON pointer into what was sent, and a sentence.</summary>
    public sealed record FieldError(string Pointer, string Message);

    public sealed record Outcome<T>(OutcomeKind Kind, T? Value, string? Code = null, string? Message = null, IReadOnlyList<FieldError>? Errors = null)
    {
        public static Outcome<T> Ok(T value) => new(OutcomeKind.Ok, value);
        public static Outcome<T> Created(T value) => new(OutcomeKind.Created, value);
        public static Outcome<T> NotFound(string message) => new(OutcomeKind.NotFound, default, ErrorCodes.NotFound, message);
        public static Outcome<T> Conflict(string code, string message) => new(OutcomeKind.Conflict, default, code, message);
        public static Outcome<T> Invalid(string code, string message, IReadOnlyList<FieldError>? errors = null)
            => new(OutcomeKind.Invalid, default, code, message, errors);
        public static Outcome<T> Unprocessable(string code, string message, IReadOnlyList<FieldError>? errors = null)
            => new(OutcomeKind.Unprocessable, default, code, message, errors);
        public static Outcome<T> Forbidden(string code, string message) => new(OutcomeKind.Forbidden, default, code, message);
    }
}
