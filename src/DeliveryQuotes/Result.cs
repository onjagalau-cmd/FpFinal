namespace DeliveryQuotes;

// Expected domain/operational failure: a code to identify the rule + a message for the caller.
public sealed record DomainError(string Code, string Message);

// Result: exactly one of two cases. Constructors are internal and the base constructor is private,
// so no other cases can exist, and a Success always has a non-null value, a Failure always has an error.
public abstract record Result<T>
{
    private Result() { }

    public sealed record Success : Result<T>
    {
        public T Value { get; }
        internal Success(T value) => Value = value;
    }

    public sealed record Failure : Result<T>
    {
        public DomainError Error { get; }
        internal Failure(DomainError error) => Error = error;
    }

    public static Result<T> Ok(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Success(value);
    }

    public static Result<T> Fail(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Failure(error);
    }

    // Map: Result<T> + (T -> U) -> Result<U>. The function runs only for Success.
    public Result<U> Map<U>(Func<T, U> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return this switch
        {
            Success s => Result<U>.Ok(function(s.Value)),
            Failure f => Result<U>.Fail(f.Error),
            _ => Result<U>.Fail(new DomainError("UNEXPECTED_CASE", "Unknown result case."))
        };
    }

    // Bind: Result<T> + (T -> Result<U>) -> Result<U>. The function runs only for Success.
    public Result<U> Bind<U>(Func<T, Result<U>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return this switch
        {
            Success s => function(s.Value),
            Failure f => Result<U>.Fail(f.Error),
            _ => Result<U>.Fail(new DomainError("UNEXPECTED_CASE", "Unknown result case."))
        };
    }
}

// Maybe: a value that may be absent. Zero, false and "" are legitimate values (Some), not absence.
public abstract record Maybe<T>
{
    private Maybe() { }

    public sealed record Some : Maybe<T>
    {
        public T Value { get; }
        internal Some(T value) => Value = value;
    }

    public sealed record None : Maybe<T>
    {
        internal None() { }
    }

    public static Maybe<T> Just(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Some(value);
    }

    public static Maybe<T> Nothing() => new None();

    // Required item: absence becomes a meaningful failure.
    public Result<T> ToResult(DomainError errorIfAbsent)
        => this switch
        {
            Some s => Result<T>.Ok(s.Value),
            None _ => Result<T>.Fail(errorIfAbsent),
            _ => Result<T>.Fail(new DomainError("UNEXPECTED_CASE", "Unknown maybe case."))
        };
}
