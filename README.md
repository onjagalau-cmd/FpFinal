Final Project: Delivery Quotes and Orders (Introduction to Functional Programming)
Author: Azamat Kazybek, group IT-2512
Repository: <URL> | Branch: main | Final commit: <commit id>
Problem, users, goals
A small delivery company needs to quote, order and bill deliveries. Dispatchers place orders for customers, finance confirms orders only within each customer's credit limit, and managers read reports. The application is a console program written with functional techniques: pure core, immutable models, explicit outcomes (Result, Maybe), LINQ reports, lazy processing, and one native C function.
Domain models (related)
- Customer (Customer: id, name, tier regular/premium, credit limit), loaded from data/customers.csv.
- Order (DeliveryRequest -> ValidatedRequest -> OrderState: Quoted / Confirmed / Rejected), loaded from data/requests.csv (id,customerId,distanceKm).
- Relation: every stored order has an owner customer (OrderBook.Owners: orderId -> customerId). Prices depend on the customer (tier rule) and confirmation depends on the customer (credit limit).
Workflows (3 required for one student; all use the same data)
#	Workflow	Input	Rules	Result
1	Place order (OrderDesk.PlaceOrder)	customer id, order id, distance, policy name, pricing rule	customer must exist, policy must exist, distance 0..50 km, id unique; price = delivery policy then customer rule	Quoted order stored with its owner, or an error
2	Confirm order (OrderDesk.ConfirmOrder)	order id	order must exist and be Quoted, must have a known customer, confirmed total + price <= credit limit; repeat returns the existing confirmation	Confirmed order, or an error
3	Reports and export (ReportWorkflow.RunAndExport)	requests file, policy, options	strict file loading, lazy quoting, one snapshot, native fuel surcharge per quote	zone report, two ranked lists, surcharge total, output/report.csv
Extras: cancel (QuoteService.CancelOrder), customer report (CustomerReports.ByCustomer), saving orders (output/orders.csv).				


Technical requirements -> code
Requirement	Where
Pure core, separated I/O	Pipeline.cs, Workflow.cs, Business.cs, Reports.cs are pure; only FileLoader, FileExporter (InputParser.cs), Program.cs do I/O
Immutable models	records and ImmutableArray/ImmutableDictionary in Models.cs
Pattern matching in a domain decision	PricingRules.PremiumDiscount (tier), OrderDesk.ConfirmOrder (order state), Workflow.cs switches
Pipeline, 3+ stages	Pipeline.SelectServiceable -> CalculateQuotes -> BuildSummary (Pipeline.Run)
Higher-order function changing a rule	Pipeline.CalculateQuotes (price policy), OrderDesk.PlaceOrder (pricing rule Func<Customer,decimal,decimal>)
Safe operation sequence	factory ValidatedRequest.Create (private constructor), closed state types OrderState, ConfirmOrder rejects missing/invalid prerequisites
Result, Map and Bind, failure skips later stages	Result.cs; chains in OrderDesk.PlaceOrder, ConfirmQuoted, ReportWorkflow.Build
Maybe for lookups	Lookup, CustomerLookup
Boundary exceptions	FileLoader.ReadDataLines, FileExporter.Save, SurchargeCalculator.Calculate
2+ extension methods	QuoteExtensions.ZoneOf, RankBy
2+ LINQ reports	Reports.ByZone (GroupBy, Sum, Count, OrderBy/ThenBy, Aggregate), Reports.TopRanked (Where, Select, OrderByDescending/ThenBy), CustomerReports.ByCustomer
Currying / partial application	Scoring.CreateScore, RevenueFocused, ProximityFocused
yield return, deferred execution, snapshot	Pipeline.QuoteStream, FileLoader.ReadLinesLazily, ReportWorkflow.Build
Disposal	using in ReadLinesLazily and FileExporter.Save
Native C library	native/dqnative.c, Native.cs
Persistence	load customers.csv, requests.csv; save output/orders.csv, export output/report.csv


Data files (data/)
customers.csv (id,name,tier,creditLimit), requests.csv (id,customerId,distanceKm), malformed.csv and customers_bad.csv (failure demos), missing.csv does not exist on purpose.
Domain rules
- Distance 0..50 km (0 is valid, 50 included). Delivery policies: standard 500 + 100/km, express 800 + 150/km.
- Pricing rule: premium customers pay 90 % (whole units, half away from zero); regular customers pay the full price.
- Credit: a customer's confirmed orders (including the new one) must not exceed the credit limit (equal is allowed).
- Zones: Local <= 10 km, Regional <= 30 km, Remote above. Native fuel surcharge: 5 % (500 basis points), whole units, rounded half up.
Error codes
FILE_NOT_FOUND, FILE_READ_ERROR, MALFORMED_CONTENT, EXPORT_FAILED, INVALID_REQUEST, CUSTOMER_NOT_FOUND, POLICY_NOT_FOUND, DUPLICATE_ORDER, ORDER_NOT_FOUND, ORDER_HAS_NO_CUSTOMER, CREDIT_LIMIT_EXCEEDED, INVALID_STATE, INVALID_NATIVE_INPUT, NATIVE_REJECTED_INPUT, NATIVE_UNAVAILABLE, MISSING_INPUT.
Expected output of dotnet run (main points)
- Files: customers 3 OK, requests 8 OK; missing.csv FAILED [FILE_NOT_FOUND]; malformed.csv FAILED [MALFORMED_CONTENT] (F6, E5); customers_bad.csv FAILED [MALFORMED_CONTENT] (gold, -5, duplicate C1).
- Place (standard + premium rule): A1 630, B2 1500, C3 4950, G7 500, H8 1500, K9 2700, L10 4000 quoted; D4 FAILED [INVALID_REQUEST] (70 km).
- Rule change: A1 with NoDiscount 700, with PremiumDiscount 630.
- Place failures: C9 [CUSTOMER_NOT_FOUND], A1 again [DUPLICATE_ORDER], 'economy' [POLICY_NOT_FOUND].
- Confirm: A1, C3, K9, B2, L10, G7 confirmed (G7 brings C2 to exactly 6000); H8 FAILED [CREDIT_LIMIT_EXCEEDED]; A1 again returns the existing confirmation; ZZ [ORDER_NOT_FOUND].
- Customer report: C1 3 orders confirmed 8280 credit left 11720; C2 3 orders 6000, left 0; C3 1 order quoted 1500, left 1000.
- Cancel: A1 [INVALID_STATE], ZZ [ORDER_NOT_FOUND], H8 cancelled. Lookup: G7 FOUND 0 km, A1 FOUND 2 km, H8 NOT FOUND, ZZ NOT FOUND.
- Save: output/orders.csv with 6 orders.
- Deferred: 0 records after creating the query, 2 after taking two quotes, 10 in total after ToImmutableArray (7 quotes), still 10 when the snapshot is reused.
- Reports (standard): Remote 2 quotes 9500; Local 4 quotes 4200; Regional 1 quote 3000; grand total 16700; native surcharge 835; revenue ranking C3, L10, K9, B2, H8; proximity ranking A1, B2, H8, K9, L10; export output/report.csv.
- Report failures: unknown policy, missing input file, missing export folder, simulated missing native library.
- Pipeline: Standard 7 quotes total 16700; Express 7 quotes total 25400.
Native C library (Tasks 7-9 of Assignment 8, kept)
- native/dqnative.c, native/dqnative.h; build with native/build.sh (Linux/macOS) or native\build.cmd (Windows, gcc or cl). Output in native/lib/.
- Contract: int64_t dq_surcharge(int64_t price_units, int32_t basis_points) returns (price * bp + 5000) / 10000 (half up), price_units 0..922337203685477, basis_points 0..10000, otherwise -1. Example: (700, 500) = 35; (1500, 250) = 38.
- C# side: NativeMethods.dq_surcharge (DllImport, cdecl) and adapter SurchargeCalculator (validation, Result, NATIVE_UNAVAILABLE on load failure; the native function can be injected for tests).
- DeliveryQuotes.csproj builds the library if it is missing and copies it next to the program (BuildNative, CopyNative); the test project copies it too. Platform of the included binary: Linux x86-64, gcc 13.3. On Windows install a 64-bit gcc (for example winget install BrechtSanders.WinLibs.POSIX.UCRT) or Visual Studio C++ tools.
Resource and retention note
Files are read inside a bounded scope into snapshots (ToArray, ImmutableArray); the StreamReader is owned by the iterator and disposed on completion, failure or early stop; reports are written to a temp file and moved. Reports share one quote snapshot. There are no caches, event subscriptions or long-lived mutable collections; static data is immutable. Trade-off: a snapshot costs memory but allows safe reuse and closes the file early; streaming saves memory but can be read once.
Tests (xUnit)
Files in tests/DeliveryQuotes.Tests: PipelineTests (pipeline, parser, purity), WorkflowTests (factory, states, safe sequence), ResultTests (Result, Maybe, Map/Bind, file boundary), ReportTests (reports, extensions, currying), LazyAndResourceTests (deferred execution, disposal, exporter), NativeTests (real native call, adapter failures), IntegrationTests (report workflow, native, export), OrderDeskTests (place, confirm, credit, pricing rule, customer report, orders file format), CustomerFileTests (customer parsing/loading, complete flows).
Required groups: pure/domain tests (reports, pricing, parsers, factory), complete workflows (NormalWorkflow_ReportsNativeCallAndExport_Integrated, CompleteFlow_FromFiles_PlaceConfirmReportAndSave), file tests (missing and malformed files for requests and customers). Coverage: normal, boundary (50 km, credit limit equal, zone limits), invalid, empty, missing, operation order, failure propagation (counter proves skipped functions).
Latest test outcome: <paste the result of dotnet test here>
Build, run, test
Prerequisites: .NET SDK 10.0 (dotnet --version) and a 64-bit C compiler for the native library (a Linux x64 binary is included in native/lib).
Run from the solution root (folder with DeliveryQuotes.sln):
dotnet build
dotnet run --project src/DeliveryQuotes
dotnet test
Optional arguments: dotnet run --project src/DeliveryQuotes -- data/requests.csv data/customers.csv. Output files go to output/.
Limitations
Console only; CSV storage; the order book is kept in memory during a run and saved at the end (orders are not reloaded); one native function; delivery policies and pricing rules are chosen in Program.cs, not by a menu.
Responsibilities
Individual project: all modules, tests and reports by the author.