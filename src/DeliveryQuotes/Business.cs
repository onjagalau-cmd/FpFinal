using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace DeliveryQuotes;

public sealed record ConfirmReceipt(OrderBook Book, OrderState.Confirmed Order);
public sealed record CustomerRow(string CustomerId, string Name, int Orders, decimal Confirmed, decimal Quoted, decimal CreditLeft);

public static class CustomerDirectory
{
    // Immutable lookup table. Ids must be unique (the file loader rejects duplicates).
    public static ImmutableDictionary<string, Customer> From(IEnumerable<Customer> customers)
        => customers.ToImmutableDictionary(c => c.Id, StringComparer.Ordinal);
}

// Maybe-based lookups: present and absent are both explicit.
public static class CustomerLookup
{
    public static Maybe<Customer> Find(ImmutableDictionary<string, Customer> customers, string? id)
        => id is not null && customers.TryGetValue(id.Trim(), out var customer)
            ? Maybe<Customer>.Just(customer)
            : Maybe<Customer>.Nothing();

    public static Maybe<string> OwnerOf(OrderBook book, string? orderId)
        => orderId is not null && book.Owners.TryGetValue(orderId.Trim(), out var owner)
            ? Maybe<string>.Just(owner)
            : Maybe<string>.Nothing();
}

// Pricing rules (customer, price after the delivery policy) -> final price.
// Passed into the workflow as a delegate: switching the rule changes the business result.
public static class PricingRules
{
    public static decimal NoDiscount(Customer customer, decimal price) => price;

    // Pattern matching on the tier: premium customers pay 90 % (whole currency units, half away from zero).
    public static decimal PremiumDiscount(Customer customer, decimal price)
        => customer.Tier switch
        {
            CustomerTier.Premium => Math.Round(price * 0.9m, 0, MidpointRounding.AwayFromZero),
            CustomerTier.Regular => price,
            _ => price
        };
}

// Workflows that connect the two domain models (Customer and Order). Pure: no Console, no files.
public static class OrderDesk
{
    // Workflow 1: place an order for a customer.
    // customer lookup -> policy lookup -> validated request -> price (policy + customer rule) -> add to book -> record owner
    public static Result<PlacedOrder> PlaceOrder(
        ImmutableDictionary<string, Customer> customers,
        OrderBook book,
        DeliveryRequest request,
        string? policyName,
        Func<Customer, decimal, decimal> pricingRule,
        decimal maxKm)
    {
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pricingRule);

        return CustomerLookup.Find(customers, request.CustomerId)
            .ToResult(new DomainError("CUSTOMER_NOT_FOUND", $"Customer '{request.CustomerId}' was not found."))
            .Bind(customer => Lookup.Policy(policyName)
                .ToResult(new DomainError("POLICY_NOT_FOUND", $"Unknown price policy '{policyName}'."))
                .Bind(policy => QuoteService.PlaceOrderWithPolicy(
                    book, request.Id, request.DistanceKm, km => pricingRule(customer, policy(km)), maxKm)))
            .Map(placed => WithOwner(placed, request.CustomerId));
    }

    private static PlacedOrder WithOwner(PlacedOrder placed, string customerId)
        => placed with
        {
            Book = placed.Book with { Owners = placed.Book.Owners.SetItem(placed.Order.Request.Id, customerId) }
        };

    // Workflow 2: confirm an order. Order must exist and be Quoted, must have a known customer,
    // and the customer's confirmed total + this order must stay within the credit limit.
    // Repeated confirmation returns the existing confirmed order and the same book.
    public static Result<ConfirmReceipt> ConfirmOrder(
        ImmutableDictionary<string, Customer> customers, OrderBook book, string? orderId)
    {
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(book);

        return Lookup.Order(book, orderId)
            .ToResult(new DomainError("ORDER_NOT_FOUND", $"Order '{orderId}' was not found."))
            .Bind(state => state switch
            {
                OrderState.Quoted quoted => ConfirmQuoted(customers, book, quoted),
                OrderState.Confirmed existing => Result<ConfirmReceipt>.Ok(new ConfirmReceipt(book, existing)),
                _ => Result<ConfirmReceipt>.Fail(new DomainError(
                    "INVALID_STATE", $"Order '{orderId}' cannot be confirmed in its current state."))
            });
    }

    private static Result<ConfirmReceipt> ConfirmQuoted(
        ImmutableDictionary<string, Customer> customers, OrderBook book, OrderState.Quoted quoted)
        => CustomerLookup.OwnerOf(book, quoted.Request.Id)
            .ToResult(new DomainError("ORDER_HAS_NO_CUSTOMER", $"Order '{quoted.Request.Id}' has no customer."))
            .Bind(customerId => CustomerLookup.Find(customers, customerId)
                .ToResult(new DomainError("CUSTOMER_NOT_FOUND", $"Customer '{customerId}' was not found.")))
            .Bind(customer => CheckCredit(customer, book, quoted))
            .Bind(approved => Commit(book, approved));

    private static Result<OrderState.Quoted> CheckCredit(Customer customer, OrderBook book, OrderState.Quoted quoted)
    {
        var committed = book.Orders.Values
            .OfType<OrderState.Confirmed>()
            .Where(c => book.Owners.TryGetValue(c.Request.Id, out var owner) && owner == customer.Id)
            .Sum(c => c.Price);

        return committed + quoted.Price <= customer.CreditLimit
            ? Result<OrderState.Quoted>.Ok(quoted)
            : Result<OrderState.Quoted>.Fail(new DomainError(
                "CREDIT_LIMIT_EXCEEDED",
                $"Customer '{customer.Id}' credit limit {customer.CreditLimit} exceeded: confirmed {committed} + order {quoted.Price}."));
    }

    private static Result<ConfirmReceipt> Commit(OrderBook book, OrderState.Quoted quoted)
        => Workflow.Confirm(quoted) switch
        {
            OrderState.Confirmed confirmed => Result<ConfirmReceipt>.Ok(new ConfirmReceipt(
                book with { Orders = book.Orders.SetItem(quoted.Request.Id, confirmed) }, confirmed)),
            _ => Result<ConfirmReceipt>.Fail(new DomainError(
                "INVALID_STATE", $"Order '{quoted.Request.Id}' could not be confirmed."))
        };
}

// Report 3 (finance): one row per customer, built from both models. Pure.
public static class CustomerReports
{
    public static ImmutableArray<CustomerRow> ByCustomer(IEnumerable<Customer> customers, OrderBook book)
        => customers
            .Select(customer =>
            {
                var mine = book.Orders
                    .Where(entry => book.Owners.TryGetValue(entry.Key, out var owner) && owner == customer.Id)
                    .Select(entry => entry.Value)
                    .ToList();
                var confirmed = mine.OfType<OrderState.Confirmed>().Sum(o => o.Price);
                var quoted = mine.OfType<OrderState.Quoted>().Sum(o => o.Price);
                return new CustomerRow(customer.Id, customer.Name, mine.Count, confirmed, quoted,
                    customer.CreditLimit - confirmed);
            })
            .OrderByDescending(row => row.Confirmed)
            .ThenBy(row => row.CustomerId, StringComparer.Ordinal)
            .ToImmutableArray();
}

// Pure formatting of the saved order data.
public static class OrderBookFormatter
{
    private static string N(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static string ToCsv(OrderBook book)
    {
        var sb = new StringBuilder();
        sb.Append("orderId,customerId,state,distanceKm,price,code\n");
        foreach (var entry in book.Orders.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var owner = book.Owners.TryGetValue(entry.Key, out var customerId) ? customerId : string.Empty;
            var line = entry.Value switch
            {
                OrderState.Quoted q => $"{entry.Key},{owner},quoted,{N(q.Request.DistanceKm)},{N(q.Price)},",
                OrderState.Confirmed c => $"{entry.Key},{owner},confirmed,{N(c.Request.DistanceKm)},{N(c.Price)},{c.Code}",
                _ => null
            };
            if (line is not null)
                sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
