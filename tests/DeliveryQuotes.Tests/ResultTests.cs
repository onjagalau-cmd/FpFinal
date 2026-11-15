using System.Collections.Immutable;
using DeliveryQuotes;
using Xunit;

public class ResultTests
{
    private const decimal Max = 50m;
    private static readonly DomainError Err = new("E_TEST", "boom");

    private static T Value<T>(Result<T> r) => Assert.IsType<Result<T>.Success>(r).Value;
    private static DomainError Error<T>(Result<T> r) => Assert.IsType<Result<T>.Failure>(r).Error;

    private static OrderBook BookWith(string id, decimal km)
        => Value(QuoteService.PlaceOrder(OrderBook.Empty, id, km, "standard", Max)).Book;

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"dq_{Guid.NewGuid():N}.csv");

    // ---------- COMPLETE SUCCESS ----------
    [Fact]
    public void CompleteSuccess_ResultChain_ProducesQuotedOrderInBook()
    {
        var placed = Value(QuoteService.PlaceOrder(OrderBook.Empty, "A1", 2m, "standard", Max));

        Assert.Equal("A1", placed.Order.Request.Id);
        Assert.Equal(700m, placed.Order.Price);
        Assert.True(placed.Book.Orders.ContainsKey("A1"));
    }

    [Fact]
    public void CompleteSuccess_PolicyNameIsCaseInsensitive_Express()
    {
        var placed = Value(QuoteService.PlaceOrder(OrderBook.Empty, "A1", 2m, "EXPRESS", Max));

        Assert.Equal(1100m, placed.Order.Price);
    }

    // ---------- DOMAIN FAILURES ----------
    [Fact]
    public void DomainFailure_InvalidRequest()
    {
        var error = Error(QuoteService.PlaceOrder(OrderBook.Empty, "D4", 70m, "standard", Max));

        Assert.Equal("INVALID_REQUEST", error.Code);
        Assert.Contains("exceeds", error.Message);
    }

    [Fact]
    public void DomainFailure_UnknownPolicy()
    {
        var error = Error(QuoteService.PlaceOrder(OrderBook.Empty, "A1", 2m, "economy", Max));

        Assert.Equal("POLICY_NOT_FOUND", error.Code);
        Assert.Contains("economy", error.Message);
    }

    [Fact]
    public void DomainFailure_DuplicateOrder()
    {
        var book = BookWith("A1", 2m);

        var error = Error(QuoteService.PlaceOrder(book, "A1", 9m, "express", Max));

        Assert.Equal("DUPLICATE_ORDER", error.Code);
        Assert.Contains("A1", error.Message);
    }

    [Fact]
    public void DomainFailure_CancelConfirmedOrder_InvalidState()
    {
        var confirmed = Workflow.ConfirmOrder(BookWith("A1", 2m), "A1").Book;

        var error = Error(QuoteService.CancelOrder(confirmed, "A1"));

        Assert.Equal("INVALID_STATE", error.Code);
    }

    [Fact]
    public void DomainFailure_CancelUnknownOrder_NotFound()
    {
        var error = Error(QuoteService.CancelOrder(OrderBook.Empty, "ZZ"));

        Assert.Equal("ORDER_NOT_FOUND", error.Code);
        Assert.Contains("ZZ", error.Message);
    }

    [Fact]
    public void Cancel_QuotedOrder_RemovesItFromNewBookOnly()
    {
        var book = BookWith("A1", 2m);

        var receipt = Value(QuoteService.CancelOrder(book, "A1"));

        Assert.Equal("A1", receipt.Id);
        Assert.Empty(receipt.Book.Orders);
        Assert.True(book.Orders.ContainsKey("A1"));
    }

    // ---------- LOOKUP PRESENT AND ABSENT ----------
    [Fact]
    public void Lookup_Present_And_Absent()
    {
        var book = BookWith("A1", 2m);

        Assert.IsType<Maybe<OrderState>.Some>(Lookup.Order(book, "A1"));
        Assert.IsType<Maybe<OrderState>.None>(Lookup.Order(book, "ZZ"));
        Assert.IsType<Maybe<OrderState>.None>(Lookup.Order(book, ""));
        Assert.IsType<Maybe<OrderState>.None>(Lookup.Order(book, null));
        Assert.IsType<Maybe<Func<decimal, decimal>>.Some>(Lookup.Policy("standard"));
        Assert.IsType<Maybe<Func<decimal, decimal>>.None>(Lookup.Policy("economy"));
    }

    [Fact]
    public void Lookup_ZeroDistance_IsPresent_NotAbsent()
    {
        var book = BookWith("G7", 0m);

        var found = Assert.IsType<Maybe<decimal>.Some>(Lookup.Distance(book, "G7"));

        Assert.Equal(0m, found.Value);
        Assert.IsType<Maybe<decimal>.None>(Lookup.Distance(book, "ZZ"));
    }

    [Fact]
    public void Maybe_ToResult_ConvertsAbsenceToFailure()
    {
        Assert.Equal(0m, Value(Maybe<decimal>.Just(0m).ToResult(Err)));
        Assert.Equal(Err, Error(Maybe<decimal>.Nothing().ToResult(Err)));
    }

    // ---------- MAP / BIND ----------
    [Fact]
    public void Map_Success_TransformsValue()
    {
        var result = Result<int>.Ok(5).Map(x => x * 2);

        Assert.Equal(10, Value(result));
    }

    [Fact]
    public void Bind_Success_PreservesNextResult()
    {
        Assert.Equal("v5", Value(Result<int>.Ok(5).Bind(x => Result<string>.Ok($"v{x}"))));
        Assert.Equal(Err, Error(Result<int>.Ok(5).Bind(x => Result<int>.Fail(Err))));
    }

    // ---------- FAILURE PROPAGATION ----------
    [Fact]
    public void MapAndBind_OnFailure_PropagateError_AndDoNotCallFunctions()
    {
        var calls = 0;
        var failed = Result<int>.Fail(Err);

        var mapped = failed.Map(x => { calls++; return x * 2; });
        var bound = failed.Bind(x => { calls++; return Result<int>.Ok(x); });

        Assert.Equal(0, calls);
        Assert.Equal(Err, Error(mapped));
        Assert.Equal(Err, Error(bound));
    }

    [Fact]
    public void FailedChain_DoesNotRunLaterSteps()
    {
        var calls = 0;

        var result = Maybe<int>.Nothing().ToResult(Err)
            .Bind(x => { calls++; return Result<int>.Ok(x); })
            .Map(x => { calls++; return x + 1; });

        Assert.Equal(0, calls);
        Assert.Equal(Err, Error(result));
    }

    [Fact]
    public void Result_CannotBeBuiltFromNullPayloadOrNullError()
    {
        string? nothing = null;
        DomainError? noError = null;

        Assert.Throws<ArgumentNullException>(() => Result<string>.Ok(nothing));
        Assert.Throws<ArgumentNullException>(() => Result<string>.Fail(noError));
    }

    // ---------- STATE PRESERVATION ----------
    [Fact]
    public void FailedOperations_LeaveOriginalBookUnchanged()
    {
        var book = BookWith("A1", 2m);

        _ = QuoteService.PlaceOrder(book, "D4", 70m, "standard", Max);
        _ = QuoteService.PlaceOrder(book, "A1", 2m, "standard", Max);
        _ = QuoteService.CancelOrder(book, "ZZ");

        Assert.Single(book.Orders);
        Assert.Equal(700m, Assert.IsType<OrderState.Quoted>(book.Orders["A1"]).Price);
    }

    // ---------- FILES ----------
    [Fact]
    public void MissingFile_IsReportedAsFailure()
    {
        var path = TempPath();

        var error = Error(FileLoader.LoadRequests(path));

        Assert.Equal("FILE_NOT_FOUND", error.Code);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void MissingDirectory_IsReportedAsFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dq_{Guid.NewGuid():N}", "x.csv");

        Assert.Equal("FILE_NOT_FOUND", Error(FileLoader.LoadRequests(path)).Code);
        Assert.Equal("FILE_NOT_FOUND", Error(FileLoader.LoadRequests(null)).Code);
    }

    [Fact]
    public void MalformedFile_BecomesFailure_WithoutPartialData()
    {
        var path = TempPath();
        File.WriteAllText(path, "A1,2\nF6,abc\nE5,-3\n");
        try
        {
            var error = Error(FileLoader.LoadRequests(path));

            Assert.Equal("MALFORMED_CONTENT", error.Code);
            Assert.Contains("F6", error.Message);
            Assert.Contains("E5", error.Message);
            Assert.Contains(path, error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidFile_IsLoaded_BlankLinesIgnored_ZeroKept()
    {
        var path = TempPath();
        File.WriteAllText(path, "A1,C1,2\n\nG7,C1,0\n");
        try
        {
            var requests = Value(FileLoader.LoadRequests(path));

            Assert.Equal(2, requests.Length);
            Assert.Equal(0m, requests[1].DistanceKm);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyFile_IsValidEmptyDataset()
    {
        var path = TempPath();
        File.WriteAllText(path, "");
        try
        {
            Assert.Empty(Value(FileLoader.LoadRequests(path)));
        }
        finally { File.Delete(path); }
    }
}
