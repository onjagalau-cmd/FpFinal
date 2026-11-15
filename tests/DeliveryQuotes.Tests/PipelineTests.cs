using DeliveryQuotes;
using Xunit;

public class PipelineTests
{
    private static List<DeliveryRequest> Sample() => new()
    {
        new("A1", 2m, "C1"), new("B2", 10m, "C1"), new("C3", 50m, "C1"), new("D4", 70m, "C1"), new("G7", 0m, "C1")
    };

    [Fact]
    public void NormalWorkflow_Standard_ProducesDocumentedResult()
    {
        var s = Pipeline.Run(Sample(), Pipeline.MaxDistanceKm, Policies.Standard);

        Assert.Equal(4, s.Count);
        Assert.Equal(8200m, s.Total);
        Assert.Equal(new[] { "G7", "A1", "B2", "C3" }, s.Quotes.Select(q => q.Id));
        Assert.Equal(new[] { 500m, 700m, 1500m, 5500m }, s.Quotes.Select(q => q.Price));
    }

    [Fact]
    public void EmptyDataset_ProducesEmptySummary()
    {
        var parsed = InputParser.ParseAll(Array.Empty<string>());
        Assert.Empty(parsed.Valid);
        Assert.Empty(parsed.Errors);

        var s = Pipeline.Run(parsed.Valid, Pipeline.MaxDistanceKm, Policies.Standard);
        Assert.Equal(0, s.Count);
        Assert.Equal(0m, s.Total);
        Assert.Empty(s.Quotes);
    }

    [Fact]
    public void Boundary_ExactlyMaxDistance_IsIncluded_JustAboveIsExcluded()
    {
        var input = new[] { new DeliveryRequest("EDGE", 50m, "C1"), new DeliveryRequest("OVER", 50.01m, "C1") };
        var selected = Pipeline.SelectServiceable(input, 50m);

        Assert.Single(selected);
        Assert.Equal("EDGE", selected[0].Id);
        Assert.Equal(5500m, Policies.Standard(50m));
    }

    [Theory]
    [InlineData("E5,C1,-3")]
    [InlineData("G7,,5")]
    [InlineData("E5,-3")]
    [InlineData("F6,abc")]
    [InlineData("onlyoneField")]
    [InlineData(",5")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidLine_IsRejected_WithMessage(string? line)
    {
        var ok = InputParser.TryParseLine(line, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void MissingInput_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InputParser.ParseAll(null));
    }

    [Fact]
    public void ZeroDistance_IsValid_NotAnError()
    {
        Assert.True(InputParser.TryParseLine("G7,C1,0", out var request, out _));
        Assert.NotNull(request);
        Assert.Equal(0m, request!.DistanceKm);
        Assert.Equal("C1", request.CustomerId);
    }

    [Fact]
    public void TwoPolicies_SameInput_DifferentResults()
    {
        var input = new[] { new DeliveryRequest("A1", 2m, "C1") };

        var standard = Pipeline.Run(input, Pipeline.MaxDistanceKm, Policies.Standard);
        var express = Pipeline.Run(input, Pipeline.MaxDistanceKm, Policies.Express);

        Assert.Equal(700m, standard.Quotes[0].Price);
        Assert.Equal(1100m, express.Quotes[0].Price);
        Assert.NotEqual(standard.Total, express.Total);
    }

    [Fact]
    public void Stages_DoNotModifyCallerOwnedInput()
    {
        var input = Sample();
        var copy = input.ToList();

        _ = Pipeline.SelectServiceable(input, 50m);
        _ = Pipeline.CalculateQuotes(input, Policies.Express);
        _ = Pipeline.Run(input, 50m, Policies.Standard);

        Assert.Equal(copy, input);
    }

    [Fact]
    public void SameInputAndPolicy_GiveSameOutput()
    {
        var a = Pipeline.Run(Sample(), 50m, Policies.Express);
        var b = Pipeline.Run(Sample(), 50m, Policies.Express);

        Assert.Equal(a.Total, b.Total);
        Assert.Equal(a.Quotes.AsEnumerable(), b.Quotes.AsEnumerable());
    }
}
