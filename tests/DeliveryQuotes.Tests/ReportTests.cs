using System.Collections.Immutable;
using DeliveryQuotes;
using Xunit;

public class ReportTests
{
    private static ImmutableArray<Quote> Quotes(params (string Id, decimal Km)[] items)
        => Pipeline.CalculateQuotes(items.Select(i => new DeliveryRequest(i.Id, i.Km, "C1")), Policies.Standard);

    // A1 700, B2 1500, C3 5500, G7 500, H8 1500, K9 3000, L10 4000 (standard policy)
    private static ImmutableArray<Quote> Sample()
        => Quotes(("A1", 2m), ("B2", 10m), ("C3", 50m), ("G7", 0m), ("H8", 10m), ("K9", 25m), ("L10", 35m));

    [Theory]
    [InlineData(0.0, Zone.Local)]
    [InlineData(10.0, Zone.Local)]
    [InlineData(10.01, Zone.Regional)]
    [InlineData(30.0, Zone.Regional)]
    [InlineData(30.01, Zone.Remote)]
    [InlineData(50.0, Zone.Remote)]
    public void ZoneOf_Extension_FollowsBoundaries(double km, Zone expected)
    {
        var quote = new Quote("X", (decimal)km, 0m);

        Assert.Equal(expected, quote.ZoneOf());
    }

    [Fact]
    public void ByZone_NormalData_GroupsCountsAndTotals_InDocumentedOrder()
    {
        var rows = Reports.ByZone(Sample());

        Assert.Equal(
            new[] { (Zone.Remote, 2, 9500m), (Zone.Local, 4, 4200m), (Zone.Regional, 1, 3000m) },
            rows.Select(r => (r.Zone, r.Count, r.Total)));
        Assert.Equal(16700m, Reports.GrandTotal(rows));
    }

    [Fact]
    public void ByZone_EmptyInput_GivesEmptyReport()
    {
        var rows = Reports.ByZone(ImmutableArray<Quote>.Empty);

        Assert.Empty(rows);
        Assert.Equal(0m, Reports.GrandTotal(rows));
    }

    [Fact]
    public void ByZone_EqualTotals_AreOrderedByZone_RegardlessOfInputOrder()
    {
        // Local: A + B = 3000, Regional: C = 3000
        var rows = Reports.ByZone(Quotes(("C", 25m), ("A", 10m), ("B", 10m)));

        Assert.Equal(new[] { Zone.Local, Zone.Regional }, rows.Select(r => r.Zone));
        Assert.All(rows, r => Assert.Equal(3000m, r.Total));
    }

    [Fact]
    public void ByZone_NullInput_IsRejected()
    {
        IEnumerable<Quote>? none = null;

        Assert.Throws<ArgumentNullException>(() => Reports.ByZone(none!));
    }

    [Fact]
    public void TopRanked_NoMatches_And_EmptyInput_GiveEmptyReport()
    {
        Assert.Empty(Reports.TopRanked(Sample(), 100000m, 5, Scoring.RevenueFocused));
        Assert.Empty(Reports.TopRanked(ImmutableArray<Quote>.Empty, 0m, 5, Scoring.RevenueFocused));
        Assert.Empty(Reports.TopRanked(Sample(), 0m, 0, Scoring.RevenueFocused));
    }

    [Fact]
    public void TopRanked_TiedScores_AreOrderedById_InAnyInputOrder()
    {
        var forward = Reports.TopRanked(Sample(), 600m, 5, Scoring.RevenueFocused);
        var reversed = Reports.TopRanked(Sample().Reverse(), 600m, 5, Scoring.RevenueFocused);

        var expected = new[] { "C3", "L10", "K9", "B2", "H8" };   // B2 and H8 tie at 1500
        Assert.Equal(expected, forward.Select(r => r.Id));
        Assert.Equal(expected, reversed.Select(r => r.Id));
    }

    [Fact]
    public void RankBy_Extension_TieBreaksByOrdinalId()
    {
        var ids = Quotes(("b", 10m), ("a", 10m), ("B", 10m)).RankBy(_ => 0m).Select(q => q.Id);

        Assert.Equal(new[] { "B", "a", "b" }, ids);
    }

    [Fact]
    public void ConfiguredScores_SameInput_GiveDifferentResults()
    {
        var c3 = new Quote("C3", 50m, 5500m);

        Assert.Equal(55m, Scoring.RevenueFocused(c3));
        Assert.Equal(-45m, Scoring.ProximityFocused(c3));
        Assert.Equal(20m, Scoring.CreateScore(2m)(1m)(new Quote("X", 10m, 1500m)));   // 2*1500/100 - 1*10

        var revenue = Reports.TopRanked(Sample(), 600m, 5, Scoring.RevenueFocused).Select(r => r.Id);
        var proximity = Reports.TopRanked(Sample(), 600m, 5, Scoring.ProximityFocused).Select(r => r.Id);

        Assert.Equal(new[] { "C3", "L10", "K9", "B2", "H8" }, revenue);
        Assert.Equal(new[] { "A1", "B2", "H8", "K9", "L10" }, proximity);
    }

    [Fact]
    public void Reports_DoNotChangeTheirInput()
    {
        var quotes = Sample();
        var before = quotes.ToList();

        _ = Reports.ByZone(quotes);
        _ = Reports.TopRanked(quotes, 0m, 5, Scoring.ProximityFocused);

        Assert.Equal(before, quotes.ToList());
    }
}
