using System.Collections.Immutable;
using DeliveryQuotes;
using Xunit;

public class WorkflowTests
{
    private const decimal Max = 50m;

    private static ValidatedRequest Valid(string id, decimal km)
        => Assert.IsType<ValidationOutcome.Valid>(ValidatedRequest.Create(id, km, Max)).Request;

    private static OrderBook BookWith(string id, decimal km)
        => Workflow.Submit(OrderBook.Empty, ValidatedRequest.Create(id, km, Max), Policies.Standard).Book;

    // FACTORY SUCCESS
    [Fact]
    public void Factory_ValidInput_CreatesValidatedValue()
    {
        var v = Valid("  A1 ", 2m);

        Assert.Equal("A1", v.Id);
        Assert.Equal(2m, v.DistanceKm);
    }

    // FACTORY REJECTION (each rule)
    [Theory]
    [InlineData("", 5, "Id is required")]
    [InlineData(null, 5, "Id is required")]
    [InlineData("X", -1, "must be >= 0")]
    [InlineData("X", 70, "exceeds the service zone")]
    public void Factory_InvalidInput_IsRejectedWithReason(string? id, int km, string expectedReason)
    {
        var outcome = ValidatedRequest.Create(id, km, Max);

        var invalid = Assert.IsType<ValidationOutcome.Invalid>(outcome);
        Assert.Contains(invalid.Reasons, r => r.Contains(expectedReason));
    }

    [Fact]
    public void Factory_ReportsAllReasons()
    {
        var invalid = Assert.IsType<ValidationOutcome.Invalid>(ValidatedRequest.Create("", -5m, Max));

        Assert.Equal(2, invalid.Reasons.Length);
    }

    // BOUNDARY VALUE
    [Fact]
    public void Boundary_ExactMax_IsValid_JustAbove_IsInvalid()
    {
        Assert.IsType<ValidationOutcome.Valid>(ValidatedRequest.Create("E", 50m, Max));
        Assert.IsType<ValidationOutcome.Invalid>(ValidatedRequest.Create("E", 50.01m, Max));
        Assert.IsType<ValidationOutcome.Valid>(ValidatedRequest.Create("Z", 0m, Max));
    }

    // DISTINCT CASES + pattern matching result
    [Fact]
    public void DistinctCases_AreReachable_AndMatchedCorrectly()
    {
        var quoted = Workflow.Quote(ValidatedRequest.Create("A1", 2m, Max), Policies.Standard);
        var confirmed = Workflow.Confirm(quoted);
        var rejected = Workflow.Quote(ValidatedRequest.Create("D4", 70m, Max), Policies.Standard);

        Assert.IsType<OrderState.Quoted>(quoted);
        Assert.IsType<OrderState.Confirmed>(confirmed);
        Assert.IsType<OrderState.Rejected>(rejected);

        Assert.Equal("QUOTED    A1: 700", Workflow.Describe(quoted));
        Assert.Equal("CONFIRMED A1: 700 (CNF-A1)", Workflow.Describe(confirmed));
        Assert.StartsWith("REJECTED", Workflow.Describe(rejected));
    }

    // NORMAL OPERATION
    [Fact]
    public void NormalOperation_SubmitThenConfirm_ProducesConfirmedOrder()
    {
        var book = BookWith("A1", 2m);

        var result = Workflow.ConfirmOrder(book, "A1");

        var confirmed = Assert.IsType<OrderState.Confirmed>(result.State);
        Assert.Equal(700m, confirmed.Price);
        Assert.Equal("CNF-A1", confirmed.Code);
        Assert.IsType<OrderState.Confirmed>(result.Book.Orders["A1"]);
    }

    // MISSING PREREQUISITE
    [Theory]
    [InlineData("ZZ")]
    [InlineData("")]
    [InlineData(null)]
    public void Confirm_WithoutSubmit_IsRejected(string? id)
    {
        var result = Workflow.ConfirmOrder(OrderBook.Empty, id);

        Assert.IsType<OrderState.Rejected>(result.State);
        Assert.Empty(result.Book.Orders);
    }

    [Fact]
    public void RejectedState_CannotBeConfirmed()
    {
        var rejected = Workflow.Quote(ValidatedRequest.Create("D4", 70m, Max), Policies.Standard);

        Assert.IsType<OrderState.Rejected>(Workflow.Confirm(rejected));
    }

    // REPEATED OPERATION
    [Fact]
    public void RepeatedConfirm_ReturnsExistingConfirmation_BookUnchanged()
    {
        var first = Workflow.ConfirmOrder(BookWith("A1", 2m), "A1");
        var second = Workflow.ConfirmOrder(first.Book, "A1");

        Assert.Equal(first.State, second.State);
        Assert.Same(first.Book, second.Book);
    }

    [Fact]
    public void DuplicateSubmit_IsRejected_BookUnchanged()
    {
        var book = BookWith("A1", 2m);

        var result = Workflow.Submit(book, ValidatedRequest.Create("A1", 9m, Max), Policies.Express);

        Assert.IsType<OrderState.Rejected>(result.State);
        Assert.Same(book, result.Book);
        Assert.Equal(700m, Assert.IsType<OrderState.Quoted>(book.Orders["A1"]).Price);
    }

    // STATE PRESERVATION
    [Fact]
    public void FailedOperations_LeaveOriginalBookUnchanged()
    {
        var book = BookWith("A1", 2m);
        var before = book.Orders.ToList();

        var invalidSubmit = Workflow.Submit(book, ValidatedRequest.Create("D4", 70m, Max), Policies.Standard);
        var missingConfirm = Workflow.ConfirmOrder(book, "ZZ");

        Assert.Same(book, invalidSubmit.Book);
        Assert.Same(book, missingConfirm.Book);
        Assert.Equal(before, book.Orders.ToList());
        Assert.IsType<OrderState.Quoted>(book.Orders["A1"]);
    }

    [Fact]
    public void SuccessfulConfirm_DoesNotChangeOldBook()
    {
        var book = BookWith("A1", 2m);

        var result = Workflow.ConfirmOrder(book, "A1");

        Assert.IsType<OrderState.Quoted>(book.Orders["A1"]);
        Assert.IsType<OrderState.Confirmed>(result.Book.Orders["A1"]);
    }
}
