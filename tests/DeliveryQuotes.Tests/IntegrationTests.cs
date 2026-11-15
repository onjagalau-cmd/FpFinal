using System.Collections.Immutable;
using DeliveryQuotes;
using Xunit;

public class IntegrationTests
{
    private const string Data = "A1,C1,2\nB2,C2,10\nC3,C1,50\nD4,C3,70\nG7,C2,0\nH8,C3,10\nK9,C1,25\nL10,C2,35\n";
    private static readonly ReportOptions Options = new(MinPrice: 600m, Top: 5, SurchargeBasisPoints: 500);

    private static ReportBundle Bundle(Result<ReportBundle> r) => Assert.IsType<Result<ReportBundle>.Success>(r).Value;
    private static DomainError Error(Result<ReportBundle> r) => Assert.IsType<Result<ReportBundle>.Failure>(r).Error;

    private static string Write(TempDir dir, string name, string content)
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void NormalWorkflow_ReportsNativeCallAndExport_Integrated()
    {
        using var dir = new TempDir();
        var input = Write(dir, "requests.csv", Data);
        var output = Path.Combine(dir.Path, "report.csv");

        var bundle = Bundle(ReportWorkflow.RunAndExport(input, output, "standard", Options, SurchargeCalculator.Native));

        Assert.Equal(7, bundle.QuoteCount);                  // D4 (70 km) is outside the zone
        Assert.Equal(16700m, bundle.GrandTotal);
        Assert.Equal(835L, bundle.TotalSurcharge);           // native: 25+35+75+75+150+200+275
        Assert.Equal(new[] { "C3", "L10", "K9", "B2", "H8" }, bundle.RevenueRanking.Select(r => r.Id));
        Assert.Equal(new[] { "A1", "B2", "H8", "K9", "L10" }, bundle.ProximityRanking.Select(r => r.Id));

        var csv = File.ReadAllText(output);
        Assert.Contains("Remote,2,9500\n", csv);
        Assert.Contains("grand_total,7,16700\n", csv);
        Assert.Contains("fuel_surcharge_total,835\n", csv);
        Assert.Contains("1,C3,50,5500,55\n", csv);
    }

    [Fact]
    public void ExpressPolicy_ChangesTheReportOnTheSameFile()
    {
        using var dir = new TempDir();
        var input = Write(dir, "requests.csv", Data);

        var standard = Bundle(ReportWorkflow.RunFromFile(input, "standard", Options, SurchargeCalculator.Native));
        var express = Bundle(ReportWorkflow.RunFromFile(input, "express", Options, SurchargeCalculator.Native));

        Assert.Equal(25400m, express.GrandTotal);
        Assert.NotEqual(standard.GrandTotal, express.GrandTotal);
        Assert.Equal(1271L, express.TotalSurcharge);         // 40+55+115+115+228+303+415
    }

    [Fact]
    public void EmptyFile_GivesEmptyReports_NotAnError()
    {
        using var dir = new TempDir();
        var input = Write(dir, "empty.csv", "");

        var bundle = Bundle(ReportWorkflow.RunFromFile(input, "standard", Options, SurchargeCalculator.Native));

        Assert.Equal(0, bundle.QuoteCount);
        Assert.Empty(bundle.Zones);
        Assert.Empty(bundle.RevenueRanking);
        Assert.Equal(0L, bundle.TotalSurcharge);
    }

    [Fact]
    public void MissingInputFile_FailsAndWritesNothing()
    {
        using var dir = new TempDir();
        var output = Path.Combine(dir.Path, "report.csv");

        var error = Error(ReportWorkflow.RunAndExport(
            Path.Combine(dir.Path, "nope.csv"), output, "standard", Options, SurchargeCalculator.Native));

        Assert.Equal("FILE_NOT_FOUND", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void MalformedInputFile_FailsAndWritesNothing()
    {
        using var dir = new TempDir();
        var input = Write(dir, "bad.csv", "A1,2\nF6,abc\n");
        var output = Path.Combine(dir.Path, "report.csv");

        var error = Error(ReportWorkflow.RunAndExport(input, output, "standard", Options, SurchargeCalculator.Native));

        Assert.Equal("MALFORMED_CONTENT", error.Code);
        Assert.False(File.Exists(output));
        Assert.Equal("A1,2\nF6,abc\n", File.ReadAllText(input));    // input untouched
    }

    [Fact]
    public void NativeFailure_StopsWorkflow_WritesNothing_ReportsProblem()
    {
        using var dir = new TempDir();
        var input = Write(dir, "requests.csv", Data);
        var output = Path.Combine(dir.Path, "report.csv");
        var broken = new SurchargeCalculator((_, _) => throw new DllNotFoundException("dqnative"));

        var error = Error(ReportWorkflow.RunAndExport(input, output, "standard", Options, broken));

        Assert.Equal("NATIVE_UNAVAILABLE", error.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void UnknownPolicy_And_MissingData_AreRejected()
    {
        using var dir = new TempDir();
        var input = Write(dir, "requests.csv", Data);

        Assert.Equal("POLICY_NOT_FOUND",
            Error(ReportWorkflow.RunFromFile(input, "economy", Options, SurchargeCalculator.Native)).Code);
        Assert.Equal("MISSING_INPUT",
            Error(ReportWorkflow.Build(default, "standard", Options, SurchargeCalculator.Native)).Code);
    }

    [Fact]
    public void ExportFailure_IsReported_AfterSuccessfulReports()
    {
        using var dir = new TempDir();
        var input = Write(dir, "requests.csv", Data);
        var output = Path.Combine(dir.Path, "no_folder", "report.csv");

        var error = Error(ReportWorkflow.RunAndExport(input, output, "standard", Options, SurchargeCalculator.Native));

        Assert.Equal("EXPORT_FAILED", error.Code);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "no_folder")));
    }

    [Fact]
    public void EarlierPipelineAndStates_StillWorkWithTheSameData()
    {
        using var dir = new TempDir();
        var requests = Assert.IsType<Result<ImmutableArray<DeliveryRequest>>.Success>(
            FileLoader.LoadRequests(Write(dir, "requests.csv", Data))).Value;

        var summary = Pipeline.Run(requests, Pipeline.MaxDistanceKm, Policies.Standard);
        var placed = QuoteService.PlaceOrder(OrderBook.Empty, "A1", 2m, "standard", Pipeline.MaxDistanceKm);

        Assert.Equal(7, summary.Count);
        Assert.Equal(16700m, summary.Total);
        Assert.IsType<Result<PlacedOrder>.Success>(placed);
    }
}
