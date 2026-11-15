using System.Collections.Immutable;
using DeliveryQuotes;

const decimal max = Pipeline.MaxDistanceKm;
var requestsPath = args.Length > 0 ? args[0] : "data/requests.csv";
var customersPath = args.Length > 1 ? args[1] : "data/customers.csv";

// ---------------- Boundary: load files ----------------
Console.WriteLine("=== LOAD FILES ===");
var customerFile = FileLoader.LoadCustomers(customersPath);
var requestFile = FileLoader.LoadRequests(requestsPath);
Report($"Load {customersPath}", customerFile, c => $"{c.Length} customers");
Report($"Load {requestsPath}", requestFile, r => $"{r.Length} requests");
Report("Load data/missing.csv", FileLoader.LoadRequests("data/missing.csv"), r => $"{r.Length} requests");
Report("Load data/malformed.csv", FileLoader.LoadRequests("data/malformed.csv"), r => $"{r.Length} requests");
Report("Load data/customers_bad.csv", FileLoader.LoadCustomers("data/customers_bad.csv"), c => $"{c.Length} customers");

if (customerFile is not Result<ImmutableArray<Customer>>.Success customersOk)
    return 1;
if (requestFile is not Result<ImmutableArray<DeliveryRequest>>.Success requestsOk)
    return 1;

var customers = CustomerDirectory.From(customersOk.Value);
var book = OrderBook.Empty;

// ---------------- Workflow 1: place orders ----------------
Console.WriteLine("\n=== WORKFLOW 1: PLACE ORDERS (standard policy, premium pricing rule) ===");
foreach (var request in requestsOk.Value)
{
    var placed = OrderDesk.PlaceOrder(customers, book, request, "standard", PricingRules.PremiumDiscount, max);
    Report($"Place {request.Id} ({request.CustomerId})", placed, p => Workflow.Describe(p.Order));
    if (placed is Result<PlacedOrder>.Success placedOk)
        book = placedOk.Value.Book;
}

Console.WriteLine("\n=== CONFIGURABLE RULE: same request, different pricing rule ===");
var demo = new DeliveryRequest("A1", 2m, "C1");
Report("A1 with NoDiscount      ", OrderDesk.PlaceOrder(customers, OrderBook.Empty, demo, "standard", PricingRules.NoDiscount, max), p => Workflow.Describe(p.Order));
Report("A1 with PremiumDiscount ", OrderDesk.PlaceOrder(customers, OrderBook.Empty, demo, "standard", PricingRules.PremiumDiscount, max), p => Workflow.Describe(p.Order));

Console.WriteLine("\n=== DOMAIN FAILURES (placing) ===");
Report("Unknown customer C9   ", OrderDesk.PlaceOrder(customers, book, new DeliveryRequest("M11", 5m, "C9"), "standard", PricingRules.PremiumDiscount, max), p => Workflow.Describe(p.Order));
Report("Duplicate order A1    ", OrderDesk.PlaceOrder(customers, book, new DeliveryRequest("A1", 2m, "C1"), "standard", PricingRules.PremiumDiscount, max), p => Workflow.Describe(p.Order));
Report("Unknown policy        ", OrderDesk.PlaceOrder(customers, book, new DeliveryRequest("M12", 5m, "C1"), "economy", PricingRules.PremiumDiscount, max), p => Workflow.Describe(p.Order));

// ---------------- Workflow 2: confirm with credit check ----------------
Console.WriteLine("\n=== WORKFLOW 2: CONFIRM ORDERS (credit limit check) ===");
foreach (var id in new[] { "A1", "C3", "K9", "B2", "L10", "G7", "H8", "A1", "ZZ" })
{
    var confirmed = OrderDesk.ConfirmOrder(customers, book, id);
    Report($"Confirm {id}", confirmed, c => Workflow.Describe(c.Order));
    if (confirmed is Result<ConfirmReceipt>.Success confirmedOk)
        book = confirmedOk.Value.Book;
}

Console.WriteLine("\n=== REPORT: customers (confirmed total, credit left) ===");
foreach (var row in CustomerReports.ByCustomer(customersOk.Value, book))
    Console.WriteLine($"{row.CustomerId,-3}{row.Name,-15}{row.Orders,2} orders  confirmed {row.Confirmed,6}  quoted {row.Quoted,5}  credit left {row.CreditLeft,6}");

Console.WriteLine("\n=== CANCEL ===");
Report("Cancel A1 (confirmed) ", QuoteService.CancelOrder(book, "A1"), c => c.Id);
Report("Cancel ZZ (missing)   ", QuoteService.CancelOrder(book, "ZZ"), c => c.Id);
var cancelled = QuoteService.CancelOrder(book, "H8");
Report("Cancel H8 (quoted)    ", cancelled, c => $"{c.Id} cancelled");
if (cancelled is Result<CancelReceipt>.Success cancelOk)
    book = cancelOk.Value.Book;

Console.WriteLine("\n=== LOOKUP (Maybe) ===");
Console.WriteLine($"Distance G7: {Show(Lookup.Distance(book, "G7"))}");
Console.WriteLine($"Distance A1: {Show(Lookup.Distance(book, "A1"))}");
Console.WriteLine($"Distance H8: {Show(Lookup.Distance(book, "H8"))}");
Console.WriteLine($"Distance ZZ: {Show(Lookup.Distance(book, "ZZ"))}");

// ---------------- Persistence: save updated data ----------------
Console.WriteLine("\n=== SAVE ORDERS ===");
Directory.CreateDirectory("output");
Report("Save output/orders.csv", FileExporter.Save("output/orders.csv", OrderBookFormatter.ToCsv(book)), p => p);

// ---------------- Deferred execution (probe lives here, not in the production code) ----------------
Console.WriteLine("\n=== DEFERRED EXECUTION ===");
var pulled = 0;
var lazyQuotes = Pipeline.QuoteStream(CountPulls(requestsOk.Value, () => pulled++), max, Policies.Standard);
Console.WriteLine($"After creating the query : {pulled} records processed");
var firstTwo = lazyQuotes.Take(2).ToArray();
Console.WriteLine($"After taking 2 quotes    : {pulled} records processed ({string.Join(", ", firstTwo.Select(q => q.Id))})");
var snapshot = lazyQuotes.ToImmutableArray();
Console.WriteLine($"After ToImmutableArray   : {pulled} records processed in total, {snapshot.Length} quotes");
var snapshotTotal = snapshot.Sum(q => q.Price);
Console.WriteLine($"Reusing the snapshot     : {pulled} records processed in total (total price {snapshotTotal})");

// ---------------- Workflow 3: reports, native call, export ----------------
Console.WriteLine("\n=== WORKFLOW 3: REPORTS (standard policy, native fuel surcharge 5%) ===");
var options = new ReportOptions(MinPrice: 600m, Top: 5, SurchargeBasisPoints: 500);
var reports = ReportWorkflow.RunAndExport(requestsPath, "output/report.csv", "standard", options, SurchargeCalculator.Native);
switch (reports)
{
    case Result<ReportBundle>.Success reportOk:
        PrintBundle(reportOk.Value);
        Console.WriteLine("Exported: output/report.csv");
        break;
    case Result<ReportBundle>.Failure reportFailed:
        Console.WriteLine($"REPORTS FAILED [{reportFailed.Error.Code}] {reportFailed.Error.Message}");
        break;
}

Console.WriteLine("\n=== REPORT FAILURES ===");
Report("Unknown policy        ", ReportWorkflow.RunAndExport(requestsPath, "output/never.csv", "economy", options, SurchargeCalculator.Native), b => $"{b.QuoteCount} quotes");
Report("Missing input file    ", ReportWorkflow.RunAndExport("data/missing.csv", "output/never.csv", "standard", options, SurchargeCalculator.Native), b => $"{b.QuoteCount} quotes");
Report("Export folder missing ", ReportWorkflow.RunAndExport(requestsPath, "no_such_dir/report.csv", "standard", options, SurchargeCalculator.Native), b => $"{b.QuoteCount} quotes");
var brokenNative = new SurchargeCalculator((_, _) => throw new DllNotFoundException("dqnative (simulated)"));
Report("Native lib unavailable", ReportWorkflow.RunAndExport(requestsPath, "output/never.csv", "standard", options, brokenNative), b => $"{b.QuoteCount} quotes");

// ---------------- Assignment 5 pipeline (still active) ----------------
PrintSummary("STANDARD", Pipeline.Run(requestsOk.Value, max, Policies.Standard));
PrintSummary("EXPRESS", Pipeline.Run(requestsOk.Value, max, Policies.Express));
return 0;

static void Report<T>(string label, Result<T> result, Func<T, string> describe)
    => Console.WriteLine(result switch
    {
        Result<T>.Success s => $"{label} -> OK: {describe(s.Value)}",
        Result<T>.Failure f => $"{label} -> FAILED [{f.Error.Code}] {f.Error.Message}",
        _ => $"{label} -> UNKNOWN RESULT"
    });

static string Show(Maybe<decimal> value)
    => value switch
    {
        Maybe<decimal>.Some s => $"FOUND {s.Value} km",
        Maybe<decimal>.None => "NOT FOUND",
        _ => "UNKNOWN"
    };

static IEnumerable<T> CountPulls<T>(IEnumerable<T> source, Action onPull)
{
    foreach (var item in source)
    {
        onPull();
        yield return item;
    }
}

static void PrintBundle(ReportBundle b)
{
    Console.WriteLine("-- Report 1: quotes by zone --");
    foreach (var z in b.Zones)
        Console.WriteLine($"{z.Zone,-9}{z.Count,3} quotes{z.Total,8}");
    Console.WriteLine($"Grand total: {b.GrandTotal} ({b.QuoteCount} quotes)");
    Console.WriteLine($"Fuel surcharge total (native C call): {b.TotalSurcharge}");
    PrintRanking("-- Report 2a: top ranked, revenue-focused --", b.RevenueRanking);
    PrintRanking("-- Report 2b: top ranked, proximity-focused --", b.ProximityRanking);
}

static void PrintRanking(string title, ImmutableArray<RankedRow> rows)
{
    Console.WriteLine(title);
    foreach (var r in rows)
        Console.WriteLine($"{r.Id,-4}{r.DistanceKm,4} km{r.Price,7}  score {r.Score}");
}

static void PrintSummary(string name, QuoteSummary summary)
{
    Console.WriteLine($"\n=== PIPELINE {name} ===");
    foreach (var q in summary.Quotes)
        Console.WriteLine($"{q.Id,-4} {q.DistanceKm,6} km -> {q.Price}");
    Console.WriteLine($"Count: {summary.Count}, Total: {summary.Total}");
}
