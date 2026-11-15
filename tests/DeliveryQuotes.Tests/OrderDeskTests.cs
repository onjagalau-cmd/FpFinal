using System.Collections.Immutable;
using DeliveryQuotes;
using Xunit;

public class OrderDeskTests
{
    private const decimal Max = 50m;

    private static readonly ImmutableDictionary<string, Customer> Customers = CustomerDirectory.From(new[]
    {
        new Customer("C1", "Aigerim Tours", CustomerTier.Premium, 20000m),
        new Customer("C2", "Nur Market", CustomerTier.Regular, 6000m),
        new Customer("C3", "Steppe Foods", CustomerTier.Regular, 1000m)
    });

    private static T Value<T>(Result<T> r) => Assert.IsType<Result<T>.Success>(r).Value;
    private static DomainError Error<T>(Result<T> r) => Assert.IsType<Result<T>.Failure>(r).Error;

    private static Result<PlacedOrder> TryPlace(
        OrderBook book, string id, string customerId, decimal km, Func<Customer, decimal, decimal>? rule = null)
        => OrderDesk.PlaceOrder(Customers, book, new DeliveryRequest(id, km, customerId), "standard",
            rule ?? PricingRules.PremiumDiscount, Max);

    private static OrderBook Place(OrderBook book, string id, string customerId, decimal km)
        => Value(TryPlace(book, id, customerId, km)).Book;

    private static OrderBook Confirm(OrderBook book, string id)
        => Value(OrderDesk.ConfirmOrder(Customers, book, id)).Book;

    // ---------- pricing rule (pure function, pattern matching on tier) ----------
    [Fact]
    public void PricingRules_PremiumGetsDiscount_RegularDoesNot_AndRoundingIsDefined()
    {
        var premium = new Customer("P", "P", CustomerTier.Premium, 0m);
        var regular = new Customer("R", "R", CustomerTier.Regular, 0m);

        Assert.Equal(630m, PricingRules.PremiumDiscount(premium, 700m));
        Assert.Equal(700m, PricingRules.PremiumDiscount(regular, 700m));
        Assert.Equal(300m, PricingRules.PremiumDiscount(premium, 333m));   // 299.7 -> 300
        Assert.Equal(0m, PricingRules.PremiumDiscount(premium, 0m));
        Assert.Equal(700m, PricingRules.NoDiscount(premium, 700m));
    }

    // ---------- workflow 1: place order ----------
    [Fact]
    public void Place_Success_StoresOrderPriceAndOwner()
    {
        var placed = Value(TryPlace(OrderBook.Empty, "A1", "C1", 2m));

        Assert.Equal(630m, placed.Order.Price);              // 500 + 100*2 = 700, premium -10 %
        Assert.Equal("C1", placed.Book.Owners["A1"]);
        Assert.IsType<OrderState.Quoted>(placed.Book.Orders["A1"]);
    }

    [Fact]
    public void Place_PricingRuleChangesResult_WhilePipelineStaysTheSame()
    {
        var none = Value(TryPlace(OrderBook.Empty, "A1", "C1", 2m, PricingRules.NoDiscount));
        var premium = Value(TryPlace(OrderBook.Empty, "A1", "C1", 2m, PricingRules.PremiumDiscount));

        Assert.Equal(700m, none.Order.Price);
        Assert.Equal(630m, premium.Order.Price);
    }

    [Fact]
    public void Place_UnknownCustomer_Fails_AndSkipsLaterStages()
    {
        var ruleCalls = 0;
        decimal CountingRule(Customer c, decimal price) { ruleCalls++; return price; }

        var error = Error(OrderDesk.PlaceOrder(Customers, OrderBook.Empty, new DeliveryRequest("M11", 5m, "C9"),
            "standard", CountingRule, Max));

        Assert.Equal("CUSTOMER_NOT_FOUND", error.Code);
        Assert.Contains("C9", error.Message);
        Assert.Equal(0, ruleCalls);
    }

    [Fact]
    public void Place_Failures_AreReported_AndBookStaysUnchanged()
    {
        var book = Place(OrderBook.Empty, "A1", "C1", 2m);

        Assert.Equal("INVALID_REQUEST", Error(TryPlace(book, "D4", "C3", 70m)).Code);
        Assert.Equal("DUPLICATE_ORDER", Error(TryPlace(book, "A1", "C1", 2m)).Code);
        Assert.Equal("POLICY_NOT_FOUND", Error(OrderDesk.PlaceOrder(
            Customers, book, new DeliveryRequest("X", 1m, "C1"), "economy", PricingRules.NoDiscount, Max)).Code);

        Assert.Single(book.Orders);
        Assert.Single(book.Owners);
    }

    // ---------- workflow 2: confirm with credit check ----------
    [Fact]
    public void Confirm_Success_MovesQuotedToConfirmed_InNewBookOnly()
    {
        var book = Place(OrderBook.Empty, "A1", "C1", 2m);

        var receipt = Value(OrderDesk.ConfirmOrder(Customers, book, "A1"));

        Assert.Equal("CNF-A1", receipt.Order.Code);
        Assert.Equal(630m, receipt.Order.Price);
        Assert.IsType<OrderState.Confirmed>(receipt.Book.Orders["A1"]);
        Assert.IsType<OrderState.Quoted>(book.Orders["A1"]);
    }

    [Fact]
    public void Confirm_CreditLimit_ExactlyAtLimitIsAllowed_OneMoreIsRejected()
    {
        // C2 limit 6000: B2 1500 + L10 4000 + G7 500 = 6000 (boundary)
        var book = OrderBook.Empty;
        book = Place(book, "B2", "C2", 10m);
        book = Place(book, "L10", "C2", 35m);
        book = Place(book, "G7", "C2", 0m);
        book = Place(book, "X1", "C2", 0m);                  // another 500

        book = Confirm(book, "B2");
        book = Confirm(book, "L10");
        book = Confirm(book, "G7");                          // total exactly 6000

        var error = Error(OrderDesk.ConfirmOrder(Customers, book, "X1"));   // would be 6500

        Assert.Equal("CREDIT_LIMIT_EXCEEDED", error.Code);
        Assert.Contains("C2", error.Message);
        Assert.IsType<OrderState.Quoted>(book.Orders["X1"]);   // state preserved
    }

    [Fact]
    public void Confirm_OrderBiggerThanCreditLimit_IsRejected()
    {
        var book = Place(OrderBook.Empty, "H8", "C3", 10m);   // 1500 > limit 1000

        Assert.Equal("CREDIT_LIMIT_EXCEEDED", Error(OrderDesk.ConfirmOrder(Customers, book, "H8")).Code);
    }

    [Fact]
    public void Confirm_Repeated_ReturnsExistingConfirmation_AndSameBook()
    {
        var book = Confirm(Place(OrderBook.Empty, "A1", "C1", 2m), "A1");

        var again = Value(OrderDesk.ConfirmOrder(Customers, book, "A1"));

        Assert.Same(book, again.Book);
        Assert.Equal("CNF-A1", again.Order.Code);
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("")]
    [InlineData(null)]
    public void Confirm_MissingOrder_IsRejected(string? id)
    {
        Assert.Equal("ORDER_NOT_FOUND", Error(OrderDesk.ConfirmOrder(Customers, OrderBook.Empty, id)).Code);
    }

    [Fact]
    public void Confirm_OrderWithoutCustomer_IsRejected_MissingPrerequisite()
    {
        var legacy = Value(QuoteService.PlaceOrder(OrderBook.Empty, "A1", 2m, "standard", Max)).Book;

        Assert.Equal("ORDER_HAS_NO_CUSTOMER", Error(OrderDesk.ConfirmOrder(Customers, legacy, "A1")).Code);
    }

    [Fact]
    public void Confirm_CustomerMissingFromDirectory_IsRejected()
    {
        var book = Place(OrderBook.Empty, "A1", "C1", 2m);
        var emptyDirectory = CustomerDirectory.From(Array.Empty<Customer>());

        Assert.Equal("CUSTOMER_NOT_FOUND", Error(OrderDesk.ConfirmOrder(emptyDirectory, book, "A1")).Code);
    }

    [Fact]
    public void Cancel_RemovesOrderAndOwner_FromNewBook_ConfirmedCannotBeCancelled()
    {
        var quoted = Place(OrderBook.Empty, "A1", "C1", 2m);
        var confirmed = Confirm(quoted, "A1");

        var receipt = Value(QuoteService.CancelOrder(quoted, "A1"));

        Assert.Empty(receipt.Book.Orders);
        Assert.Empty(receipt.Book.Owners);
        Assert.Single(quoted.Owners);
        Assert.Equal("INVALID_STATE", Error(QuoteService.CancelOrder(confirmed, "A1")).Code);
    }

    // ---------- Maybe lookups ----------
    [Fact]
    public void CustomerLookup_PresentAndAbsent()
    {
        Assert.IsType<Maybe<Customer>.Some>(CustomerLookup.Find(Customers, "C1"));
        Assert.IsType<Maybe<Customer>.Some>(CustomerLookup.Find(Customers, " C2 "));
        Assert.IsType<Maybe<Customer>.None>(CustomerLookup.Find(Customers, "C9"));
        Assert.IsType<Maybe<Customer>.None>(CustomerLookup.Find(Customers, null));
        Assert.IsType<Maybe<string>.None>(CustomerLookup.OwnerOf(OrderBook.Empty, "A1"));
    }

    // ---------- report 3 ----------
    [Fact]
    public void CustomerReport_CombinesBothModels_InDeterministicOrder()
    {
        var book = OrderBook.Empty;
        book = Place(book, "A1", "C1", 2m);      // 630
        book = Place(book, "C3", "C1", 50m);     // 4950
        book = Place(book, "B2", "C2", 10m);     // 1500
        book = Place(book, "H8", "C3", 10m);     // 1500 (quoted only)
        book = Confirm(book, "A1");
        book = Confirm(book, "C3");
        book = Confirm(book, "B2");

        var rows = CustomerReports.ByCustomer(Customers.Values, book);

        Assert.Equal(new[] { "C1", "C2", "C3" }, rows.Select(r => r.CustomerId));
        Assert.Equal((2, 5580m, 0m, 14420m), (rows[0].Orders, rows[0].Confirmed, rows[0].Quoted, rows[0].CreditLeft));
        Assert.Equal((1, 1500m, 0m, 4500m), (rows[1].Orders, rows[1].Confirmed, rows[1].Quoted, rows[1].CreditLeft));
        Assert.Equal((1, 0m, 1500m, 1000m), (rows[2].Orders, rows[2].Confirmed, rows[2].Quoted, rows[2].CreditLeft));
    }

    [Fact]
    public void CustomerReport_EmptyBook_ListsCustomersWithZeros_AndTiesAreOrderedById()
    {
        var rows = CustomerReports.ByCustomer(Customers.Values, OrderBook.Empty);

        Assert.Equal(new[] { "C1", "C2", "C3" }, rows.Select(r => r.CustomerId));   // all 0 confirmed: ordered by id
        Assert.All(rows, r => Assert.Equal(0, r.Orders));
        Assert.Empty(CustomerReports.ByCustomer(Array.Empty<Customer>(), OrderBook.Empty));
    }

    // ---------- persistence format ----------
    [Fact]
    public void OrderBookFormatter_WritesOrdersSortedById()
    {
        var book = OrderBook.Empty;
        book = Place(book, "B2", "C2", 10m);
        book = Place(book, "A1", "C1", 2m);
        book = Confirm(book, "A1");

        var csv = OrderBookFormatter.ToCsv(book);

        Assert.Equal(
            "orderId,customerId,state,distanceKm,price,code\n" +
            "A1,C1,confirmed,2,630,CNF-A1\n" +
            "B2,C2,quoted,10,1500,\n",
            csv);
        Assert.Equal("orderId,customerId,state,distanceKm,price,code\n", OrderBookFormatter.ToCsv(OrderBook.Empty));
    }
}
