using System.Collections.Immutable;

namespace DeliveryQuotes;

// ---- from Models ----
// Valid request (already parsed, not raw text)
public sealed record DeliveryRequest(string Id, decimal DistanceKm, string CustomerId);

// Result of stage 2
public sealed record Quote(string Id, decimal DistanceKm, decimal Price);

// Result of stage 3
public sealed record QuoteSummary(ImmutableArray<Quote> Quotes, int Count, decimal Total);

// Result of parsing at the application boundary
public sealed record ParseOutcome(ImmutableArray<DeliveryRequest> Valid, ImmutableArray<string> Errors);

// ---- from ValidatedRequest ----
// A request that passed all domain rules.
// The constructor is private, so the ONLY way to obtain an instance is Create().
// Properties have no setters, so a created value cannot be changed afterwards.
public sealed record ValidatedRequest
{
    public string Id { get; }
    public decimal DistanceKm { get; }

    private ValidatedRequest(string id, decimal distanceKm)
    {
        Id = id;
        DistanceKm = distanceKm;
    }

    // Factory: returns Valid with the value, or Invalid with ALL reasons. Never a partial value.
    public static ValidationOutcome Create(string? id, decimal distanceKm, decimal maxDistanceKm)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        var cleanId = id?.Trim() ?? string.Empty;

        if (cleanId.Length == 0)
            reasons.Add("Id is required.");
        if (distanceKm < 0)
            reasons.Add($"Distance must be >= 0 but was {distanceKm}.");
        if (distanceKm > maxDistanceKm)
            reasons.Add($"Distance {distanceKm} exceeds the service zone limit of {maxDistanceKm} km.");

        return reasons.Count == 0
            ? new ValidationOutcome.Valid(new ValidatedRequest(cleanId, distanceKm))
            : new ValidationOutcome.Invalid(reasons.ToImmutable());
    }
}

// ---- from ValidationOutcome ----
// Closed hierarchy (private constructor): only the two nested cases exist.
//   Valid   - carries the validated value
//   Invalid - carries the reasons
public abstract record ValidationOutcome
{
    private ValidationOutcome() { }

    public sealed record Valid(ValidatedRequest Request) : ValidationOutcome;
    public sealed record Invalid(ImmutableArray<string> Reasons) : ValidationOutcome;
}

// ---- from OrderState ----
// Closed hierarchy of workflow states. Each case carries only its own data.
//   Quoted    - validated request + calculated price, waiting for confirmation
//   Confirmed - same data + confirmation code
//   Rejected  - reasons why the operation was refused
public abstract record OrderState
{
    private OrderState() { }

    public sealed record Quoted(ValidatedRequest Request, decimal Price) : OrderState;
    public sealed record Confirmed(ValidatedRequest Request, decimal Price, string Code) : OrderState;
    public sealed record Rejected(ImmutableArray<string> Reasons) : OrderState;
}

// ---- from OrderBook ----
// Immutable storage of orders. Operations return a NEW book; the old one is never changed.
public sealed record OrderBook(ImmutableDictionary<string, OrderState> Orders)
{
    // orderId -> customerId (the relation between the Order and Customer models)
    public ImmutableDictionary<string, string> Owners { get; init; } = ImmutableDictionary<string, string>.Empty;

    public static OrderBook Empty { get; } = new(ImmutableDictionary<string, OrderState>.Empty);
}

// New book + the state produced by the operation.
// On failure the book is the SAME instance that was passed in.
public sealed record TransitionResult(OrderBook Book, OrderState State);

// ---- Second domain model: customer ----
public enum CustomerTier { Regular, Premium }

public sealed record Customer(string Id, string Name, CustomerTier Tier, decimal CreditLimit);
