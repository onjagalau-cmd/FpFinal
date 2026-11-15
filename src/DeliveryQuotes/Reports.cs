using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace DeliveryQuotes;

public enum Zone { Local, Regional, Remote }

public sealed record ZoneRow(Zone Zone, int Count, decimal Total);
public sealed record RankedRow(string Id, decimal DistanceKm, decimal Price, decimal Score);
public sealed record ReportOptions(decimal MinPrice, int Top, int SurchargeBasisPoints);
public sealed record ReportBundle(
    int QuoteCount,
    ImmutableArray<ZoneRow> Zones,
    decimal GrandTotal,
    ImmutableArray<RankedRow> RevenueRanking,
    ImmutableArray<RankedRow> ProximityRanking,
    long TotalSurcharge);

// ---------- Custom extension methods ----------
public static class QuoteExtensions
{
    // Receiver Quote: a quote knows its distance, so the delivery zone is derived from the quote itself.
    //   <= 10 km Local, <= 30 km Regional, otherwise Remote.
    public static Zone ZoneOf(this Quote quote)
        => quote.DistanceKm switch
        {
            <= 10m => Zone.Local,
            <= 30m => Zone.Regional,
            _ => Zone.Remote
        };

    // Receiver IEnumerable<Quote>: reusable ranking query. Highest score first; ties broken by Id (ordinal),
    // so the order is deterministic. Returns a new ordered sequence, the source is not changed.
    public static IOrderedEnumerable<Quote> RankBy(this IEnumerable<Quote> quotes, Func<Quote, decimal> score)
        => quotes.OrderByDescending(score).ThenBy(q => q.Id, StringComparer.Ordinal);
}

// ---------- Currying and partial application ----------
public static class Scoring
{
    // CreateScore : priceWeight -> distancePenalty -> (Quote -> score)
    //   score = priceWeight * price / 100 - distancePenalty * distanceKm
    public static Func<decimal, Func<decimal, Func<Quote, decimal>>> CreateScore { get; } =
        priceWeight => distancePenalty => quote =>
            priceWeight * quote.Price / 100m - distancePenalty * quote.DistanceKm;

    // Partial application: two configured functions on the same type Quote -> decimal.
    public static Func<Quote, decimal> RevenueFocused { get; } = CreateScore(1m)(0m);    // distance ignored
    public static Func<Quote, decimal> ProximityFocused { get; } = CreateScore(1m)(2m); // 2 points lost per km
}

// ---------- Pure reports (no Console, no files) ----------
public static class Reports
{
    // Report 1 (dispatcher): quotes per zone. GroupBy -> Select (Count, Sum) -> OrderBy/ThenBy.
    public static ImmutableArray<ZoneRow> ByZone(IEnumerable<Quote> quotes)
        => quotes
            .GroupBy(q => q.ZoneOf())
            .Select(g => new ZoneRow(g.Key, g.Count(), g.Sum(q => q.Price)))
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Zone)
            .ToImmutableArray();

    public static decimal GrandTotal(IEnumerable<ZoneRow> rows)
        => rows.Aggregate(0m, (sum, row) => sum + row.Total);

    // Report 2 (sales manager): ranked list. Where -> RankBy (OrderByDescending/ThenBy) -> Select -> Take.
    // The scoring function is passed in (configured outside).
    public static ImmutableArray<RankedRow> TopRanked(
        IEnumerable<Quote> quotes, decimal minPrice, int top, Func<Quote, decimal> score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return quotes
            .Where(q => q.Price >= minPrice)
            .RankBy(score)
            .Select(q => new RankedRow(q.Id, q.DistanceKm, q.Price, score(q)))
            .Take(Math.Max(0, top))
            .ToImmutableArray();
    }
}

// Pure formatting of the export file.
public static class ReportFormatter
{
    private static string N(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static string ToCsv(ReportBundle bundle)
    {
        var sb = new StringBuilder();
        sb.Append("report,zone_summary\n");
        sb.Append("zone,count,total\n");
        foreach (var z in bundle.Zones)
            sb.Append($"{z.Zone},{z.Count},{N(z.Total)}\n");
        sb.Append($"grand_total,{bundle.QuoteCount},{N(bundle.GrandTotal)}\n");
        sb.Append($"fuel_surcharge_total,{bundle.TotalSurcharge}\n");
        AppendRanking(sb, "ranking_revenue_focused", bundle.RevenueRanking);
        AppendRanking(sb, "ranking_proximity_focused", bundle.ProximityRanking);
        return sb.ToString();
    }

    private static void AppendRanking(StringBuilder sb, string name, ImmutableArray<RankedRow> rows)
    {
        sb.Append($"report,{name}\n");
        sb.Append("rank,id,distanceKm,price,score\n");
        for (var i = 0; i < rows.Length; i++)
            sb.Append($"{i + 1},{rows[i].Id},{N(rows[i].DistanceKm)},{N(rows[i].Price)},{N(rows[i].Score)}\n");
    }
}

// ---------- Integrated workflow: Result chain + reports + native call ----------
public static class ReportWorkflow
{
    public static Result<ReportBundle> Build(
        ImmutableArray<DeliveryRequest> requests, string? policyName, ReportOptions options, SurchargeCalculator surcharge)
    {
        if (requests.IsDefault)
            return Result<ReportBundle>.Fail(new DomainError("MISSING_INPUT", "No request data was given."));

        return Lookup.Policy(policyName)
            .ToResult(new DomainError("POLICY_NOT_FOUND", $"Unknown price policy '{policyName}'."))
            // Map + deliberate materialisation point: the lazy stream becomes ONE snapshot used by every report.
            .Map(policy => Pipeline.QuoteStream(requests, Pipeline.MaxDistanceKm, policy).ToImmutableArray())
            // Bind: the native call may fail, so the next step returns a Result.
            .Bind(quotes => TotalSurcharge(quotes, options.SurchargeBasisPoints, surcharge)
                .Map(total => Assemble(quotes, options, total)));
    }

    private static Result<long> TotalSurcharge(
        ImmutableArray<Quote> quotes, int basisPoints, SurchargeCalculator surcharge)
        => quotes.Aggregate(
            Result<long>.Ok(0L),
            (acc, quote) => acc.Bind(sum => surcharge.Calculate(quote.Price, basisPoints).Map(s => sum + s)));

    private static ReportBundle Assemble(ImmutableArray<Quote> quotes, ReportOptions options, long totalSurcharge)
    {
        var zones = Reports.ByZone(quotes);
        return new ReportBundle(
            quotes.Length,
            zones,
            Reports.GrandTotal(zones),
            Reports.TopRanked(quotes, options.MinPrice, options.Top, Scoring.RevenueFocused),
            Reports.TopRanked(quotes, options.MinPrice, options.Top, Scoring.ProximityFocused),
            totalSurcharge);
    }

    public static Result<ReportBundle> RunFromFile(
        string? inputPath, string? policyName, ReportOptions options, SurchargeCalculator surcharge)
        => FileLoader.LoadRequests(inputPath)
            .Bind(requests => Build(requests, policyName, options, surcharge));

    // Nothing is written unless every earlier step succeeded.
    public static Result<ReportBundle> RunAndExport(
        string? inputPath, string? outputPath, string? policyName, ReportOptions options, SurchargeCalculator surcharge)
        => RunFromFile(inputPath, policyName, options, surcharge)
            .Bind(bundle => FileExporter.Save(outputPath, ReportFormatter.ToCsv(bundle)).Map(_ => bundle));
}
