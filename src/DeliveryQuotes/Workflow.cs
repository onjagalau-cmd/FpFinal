using System.Collections.Immutable;

namespace DeliveryQuotes;

// Pure domain transitions. No Console, no files, no clock, no mutation of inputs.
public static class Workflow
{
    private static OrderState.Rejected Reject(string reason)
        => new(ImmutableArray.Create(reason));

    // ValidationOutcome -> OrderState. Needs a VALIDATED outcome and a price policy (explicit inputs).
    public static OrderState Quote(ValidationOutcome outcome, Func<decimal, decimal> pricePolicy)
        => outcome switch
        {
            ValidationOutcome.Valid v => new OrderState.Quoted(v.Request, pricePolicy(v.Request.DistanceKm)),
            ValidationOutcome.Invalid i => new OrderState.Rejected(i.Reasons),
            _ => Reject("Unexpected validation outcome.")
        };

    // Confirmation of a single state.
    //   Quoted    -> Confirmed
    //   Confirmed -> same Confirmed (repeat rule: idempotent, returns the existing outcome)
    //   Rejected  -> Rejected (a rejected order can never be confirmed)
    public static OrderState Confirm(OrderState state)
        => state switch
        {
            OrderState.Quoted q => new OrderState.Confirmed(q.Request, q.Price, $"CNF-{q.Request.Id}"),
            OrderState.Confirmed c => c,
            OrderState.Rejected r => new OrderState.Rejected(r.Reasons.Add("A rejected order cannot be confirmed.")),
            _ => Reject("Unexpected order state.")
        };

    // Adds a quoted order to the book. Duplicate ids and invalid requests leave the book unchanged.
    public static TransitionResult Submit(
        OrderBook book, ValidationOutcome outcome, Func<decimal, decimal> pricePolicy)
    {
        var state = Quote(outcome, pricePolicy);
        return state switch
        {
            OrderState.Quoted q when book.Orders.ContainsKey(q.Request.Id)
                => new TransitionResult(book, Reject($"Order '{q.Request.Id}' already exists.")),
            OrderState.Quoted q
                => new TransitionResult(book with { Orders = book.Orders.Add(q.Request.Id, q) }, q),
            OrderState.Rejected r => new TransitionResult(book, r),
            _ => new TransitionResult(book, Reject("Unexpected order state."))
        };
    }

    // Confirms a stored order. Missing prerequisite (order not submitted) -> rejected, book unchanged.
    public static TransitionResult ConfirmOrder(OrderBook book, string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !book.Orders.TryGetValue(id.Trim(), out var current))
            return new TransitionResult(book, Reject($"Order '{id}' was not submitted, nothing to confirm."));

        var key = id.Trim();
        var next = Confirm(current);

        return next switch
        {
            OrderState.Confirmed c when current is OrderState.Quoted
                => new TransitionResult(book with { Orders = book.Orders.SetItem(key, c) }, c),
            OrderState.Confirmed c => new TransitionResult(book, c),   // repeated confirmation: no change
            OrderState.Rejected r => new TransitionResult(book, r),
            _ => new TransitionResult(book, Reject("Unexpected order state."))
        };
    }

    // Text for the user, built by matching every case.
    public static string Describe(OrderState state)
        => state switch
        {
            OrderState.Quoted q => $"QUOTED    {q.Request.Id}: {q.Price}",
            OrderState.Confirmed c => $"CONFIRMED {c.Request.Id}: {c.Price} ({c.Code})",
            OrderState.Rejected r => $"REJECTED  {string.Join(" ", r.Reasons)}",
            _ => "UNKNOWN STATE"
        };
}

public sealed record PlacedOrder(OrderBook Book, OrderState.Quoted Order);
public sealed record CancelReceipt(OrderBook Book, string Id);

// Lookups return Maybe: present and absent are both explicit cases.
public static class Lookup
{
    private static readonly ImmutableDictionary<string, Func<decimal, decimal>> PolicyTable =
        new Dictionary<string, Func<decimal, decimal>>(StringComparer.OrdinalIgnoreCase)
        {
            ["standard"] = Policies.Standard,
            ["express"] = Policies.Express
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static Maybe<Func<decimal, decimal>> Policy(string? name)
        => name is not null && PolicyTable.TryGetValue(name.Trim(), out var policy)
            ? Maybe<Func<decimal, decimal>>.Just(policy)
            : Maybe<Func<decimal, decimal>>.Nothing();

    public static Maybe<OrderState> Order(OrderBook book, string? id)
        => id is not null && book.Orders.TryGetValue(id.Trim(), out var state)
            ? Maybe<OrderState>.Just(state)
            : Maybe<OrderState>.Nothing();

    // Distance 0 is a legitimate present value (Some 0), not "absent".
    public static Maybe<decimal> Distance(OrderBook book, string? id)
        => Order(book, id) switch
        {
            Maybe<OrderState>.Some s => s.Value switch
            {
                OrderState.Quoted q => Maybe<decimal>.Just(q.Request.DistanceKm),
                OrderState.Confirmed c => Maybe<decimal>.Just(c.Request.DistanceKm),
                _ => Maybe<decimal>.Nothing()
            },
            _ => Maybe<decimal>.Nothing()
        };
}

// Result-based operations built with Map and Bind. Pure: they return new values, never mutate.
public static class QuoteService
{
    private static Result<ValidatedRequest> Validate(string? id, decimal km, decimal maxKm)
        => ValidatedRequest.Create(id, km, maxKm) switch
        {
            ValidationOutcome.Valid v => Result<ValidatedRequest>.Ok(v.Request),
            ValidationOutcome.Invalid i => Result<ValidatedRequest>.Fail(
                new DomainError("INVALID_REQUEST", string.Join(" ", i.Reasons))),
            _ => Result<ValidatedRequest>.Fail(new DomainError("UNEXPECTED_CASE", "Unknown validation outcome."))
        };

    private static Result<PlacedOrder> AddToBook(OrderBook book, OrderState.Quoted quoted)
        => Lookup.Order(book, quoted.Request.Id) switch
        {
            Maybe<OrderState>.Some => Result<PlacedOrder>.Fail(
                new DomainError("DUPLICATE_ORDER", $"Order '{quoted.Request.Id}' already exists.")),
            Maybe<OrderState>.None => Result<PlacedOrder>.Ok(new PlacedOrder(
                book with { Orders = book.Orders.Add(quoted.Request.Id, quoted) }, quoted)),
            _ => Result<PlacedOrder>.Fail(new DomainError("UNEXPECTED_CASE", "Unknown lookup case."))
        };

    // Chain:  find policy (Maybe -> Result)
    //         -> Bind: validate request, then Map: price it into a Quoted state (pure transformation)
    //         -> Bind: add to the book (may fail: duplicate)
    public static Result<PlacedOrder> PlaceOrder(
        OrderBook book, string? id, decimal km, string? policyName, decimal maxKm)
        => Lookup.Policy(policyName)
            .ToResult(new DomainError("POLICY_NOT_FOUND", $"Unknown price policy '{policyName}'."))
            .Bind(policy => PlaceOrderWithPolicy(book, id, km, policy, maxKm));

    // Same chain with an already chosen price function (the final application composes policy + customer rule).
    public static Result<PlacedOrder> PlaceOrderWithPolicy(
        OrderBook book, string? id, decimal km, Func<decimal, decimal> policy, decimal maxKm)
        => Validate(id, km, maxKm)
            .Map(request => new OrderState.Quoted(request, policy(request.DistanceKm)))
            .Bind(quoted => AddToBook(book, quoted));

    // Only a Quoted order can be cancelled. Confirmed -> INVALID_STATE, unknown -> ORDER_NOT_FOUND.
    public static Result<CancelReceipt> CancelOrder(OrderBook book, string? id)
        => Lookup.Order(book, id)
            .ToResult(new DomainError("ORDER_NOT_FOUND", $"Order '{id}' was not found."))
            .Bind(state => state switch
            {
                OrderState.Quoted q => Result<CancelReceipt>.Ok(new CancelReceipt(
                    book with { Orders = book.Orders.Remove(q.Request.Id), Owners = book.Owners.Remove(q.Request.Id) }, q.Request.Id)),
                OrderState.Confirmed c => Result<CancelReceipt>.Fail(new DomainError(
                    "INVALID_STATE", $"Order '{c.Request.Id}' is confirmed and cannot be cancelled.")),
                _ => Result<CancelReceipt>.Fail(new DomainError(
                    "INVALID_STATE", $"Order '{id}' is in a state that cannot be cancelled."))
            });
}
