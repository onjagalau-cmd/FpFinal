using System.Collections.Immutable;

namespace DeliveryQuotes;

public static class Policies
{
    public static decimal Standard(decimal km) => 500m + 100m * km;
    public static decimal Express(decimal km) => 800m + 150m * km;
}

public static class Pipeline
{
    public const decimal MaxDistanceKm = 50m;

    // STAGE 1: keep requests inside the service zone (boundary included)
    public static ImmutableArray<DeliveryRequest> SelectServiceable(
        IEnumerable<DeliveryRequest> requests, decimal maxDistanceKm)
        => requests.Where(r => r.DistanceKm <= maxDistanceKm).ToImmutableArray();

    // STAGE 2: higher-order function, calls the pricePolicy delegate
    public static ImmutableArray<Quote> CalculateQuotes(
        IEnumerable<DeliveryRequest> requests, Func<decimal, decimal> pricePolicy)
        => requests
            .Select(r => new Quote(r.Id, r.DistanceKm, pricePolicy(r.DistanceKm)))
            .ToImmutableArray();

    // STAGE 3: order by price, count, total
    public static QuoteSummary BuildSummary(IEnumerable<Quote> quotes)
    {
        var ordered = quotes
            .OrderBy(q => q.Price)
            .ThenBy(q => q.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        return new QuoteSummary(ordered, ordered.Length, ordered.Sum(q => q.Price));
    }

    // LAZY stages 1+2 in one iterator: nothing runs until the caller enumerates the result.
    // Arguments are checked immediately (the iterator body is in a separate local function).
    // Enumerating this twice runs the work twice; call ToImmutableArray() to take a stable snapshot.
    public static IEnumerable<Quote> QuoteStream(
        IEnumerable<DeliveryRequest> requests, decimal maxDistanceKm, Func<decimal, decimal> pricePolicy)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(pricePolicy);
        return Stream(requests, maxDistanceKm, pricePolicy);

        static IEnumerable<Quote> Stream(
            IEnumerable<DeliveryRequest> source, decimal max, Func<decimal, decimal> policy)
        {
            foreach (var request in source)
                if (request.DistanceKm <= max)
                    yield return new Quote(request.Id, request.DistanceKm, policy(request.DistanceKm));
        }
    }

    // Composition
    public static QuoteSummary Run(
        IEnumerable<DeliveryRequest> requests, decimal maxDistanceKm, Func<decimal, decimal> pricePolicy)
        => BuildSummary(CalculateQuotes(SelectServiceable(requests, maxDistanceKm), pricePolicy));
}
