using System.Collections.Immutable;
using DeliveryQuotes;
using Xunit;

public class CustomerFileTests
{
    private static T Value<T>(Result<T> r) => Assert.IsType<Result<T>.Success>(r).Value;
    private static DomainError Error<T>(Result<T> r) => Assert.IsType<Result<T>.Failure>(r).Error;

    private static string Write(TempDir dir, string name, string content)
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void CustomerParser_ValidLine_CreatesCustomer_ZeroCreditIsLegitimate()
    {
        Assert.True(CustomerParser.TryParseLine("C1,Aigerim Tours,Premium,20000", out var c, out _));
        Assert.Equal(new Customer("C1", "Aigerim Tours", CustomerTier.Premium, 20000m), c);

        Assert.True(CustomerParser.TryParseLine("C2,Nur,regular,0", out var zero, out _));
        Assert.Equal(0m, zero!.CreditLimit);
    }

    [Theory]
    [InlineData("C1,Name,gold,100")]
    [InlineData("C1,Name,5,100")]
    [InlineData("C1,Name,regular,-1")]
    [InlineData("C1,Name,regular,abc")]
    [InlineData("C1,,regular,100")]
    [InlineData(",Name,regular,100")]
    [InlineData("C1,Name,regular")]
    [InlineData("")]
    [InlineData(null)]
    public void CustomerParser_InvalidLine_IsRejectedWithMessage(string? line)
    {
        Assert.False(CustomerParser.TryParseLine(line, out var customer, out var error));
        Assert.Null(customer);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void CustomerParser_DuplicateIds_AreReported()
    {
        var (valid, errors) = CustomerParser.ParseAll(new[] { "C1,A,regular,1", "C1,B,regular,2" });

        Assert.Single(valid);
        Assert.Contains("Duplicate", Assert.Single(errors));
        Assert.Throws<ArgumentNullException>(() => CustomerParser.ParseAll(null));
    }

    [Fact]
    public void LoadCustomers_ValidFile_EmptyFile_MissingFile()
    {
        using var dir = new TempDir();

        var customers = Value(FileLoader.LoadCustomers(Write(dir, "c.csv", "C1,A,premium,10\n\nC2,B,regular,0\n")));
        var empty = Value(FileLoader.LoadCustomers(Write(dir, "empty.csv", "")));
        var missing = Error(FileLoader.LoadCustomers(Path.Combine(dir.Path, "nope.csv")));

        Assert.Equal(2, customers.Length);
        Assert.Empty(empty);
        Assert.Equal("FILE_NOT_FOUND", missing.Code);
    }

    [Fact]
    public void LoadCustomers_MalformedFile_IsOneFailure_WithoutPartialData()
    {
        using var dir = new TempDir();
        var path = Write(dir, "bad.csv", "C1,Good,premium,100\nC2,Bad,gold,100\nC3,Neg,regular,-5\nC1,Dup,regular,5\n");

        var error = Error(FileLoader.LoadCustomers(path));

        Assert.Equal("MALFORMED_CONTENT", error.Code);
        Assert.Contains("gold", error.Message);
        Assert.Contains("Duplicate", error.Message);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void CompleteFlow_FromFiles_PlaceConfirmReportAndSave()
    {
        using var dir = new TempDir();
        var customersFile = Write(dir, "customers.csv", "C1,Aigerim Tours,premium,20000\nC2,Nur Market,regular,6000\nC3,Steppe Foods,regular,1000\n");
        var requestsFile = Write(dir, "requests.csv", "A1,C1,2\nB2,C2,10\nC3,C1,50\nD4,C3,70\nG7,C2,0\nH8,C3,10\n");
        var ordersFile = Path.Combine(dir.Path, "orders.csv");

        var customers = CustomerDirectory.From(Value(FileLoader.LoadCustomers(customersFile)));
        var book = OrderBook.Empty;
        foreach (var request in Value(FileLoader.LoadRequests(requestsFile)))
        {
            var placed = OrderDesk.PlaceOrder(customers, book, request, "standard", PricingRules.PremiumDiscount, 50m);
            if (placed is Result<PlacedOrder>.Success ok)
                book = ok.Value.Book;                          // D4 (70 km) fails and is skipped
        }
        foreach (var id in new[] { "A1", "C3", "B2", "G7", "H8" })
        {
            var confirmed = OrderDesk.ConfirmOrder(customers, book, id);
            if (confirmed is Result<ConfirmReceipt>.Success ok)
                book = ok.Value.Book;                          // H8 exceeds credit and stays quoted
        }

        Assert.Equal(5, book.Orders.Count);
        Assert.IsType<OrderState.Quoted>(book.Orders["H8"]);
        Assert.Equal(new[] { 5580m, 2000m, 0m },
            CustomerReports.ByCustomer(customers.Values, book).Select(r => r.Confirmed));

        Assert.IsType<Result<string>.Success>(FileExporter.Save(ordersFile, OrderBookFormatter.ToCsv(book)));
        var lines = File.ReadAllLines(ordersFile);
        Assert.Equal(6, lines.Length);                        // header + 5 orders
        Assert.Equal("A1,C1,confirmed,2,630,CNF-A1", lines[1]);
        Assert.Equal("H8,C3,quoted,10,1500,", lines[5]);
    }

    [Fact]
    public void CompleteFlow_FailureInInput_LeavesNothingSaved()
    {
        using var dir = new TempDir();
        var ordersFile = Path.Combine(dir.Path, "orders.csv");

        var loaded = FileLoader.LoadRequests(Write(dir, "requests.csv", "A1,C1,2\nbroken line\n"));

        Assert.Equal("MALFORMED_CONTENT", Error(loaded).Code);
        Assert.False(File.Exists(ordersFile));
    }
}
