using System.Collections.Immutable;
using DeliveryQuotes;
using Xunit;

public class LazyAndResourceTests
{
    private static readonly DeliveryRequest[] Requests =
    {
        new("A1", 2m, "C1"), new("B2", 10m, "C1"), new("D4", 70m, "C1"), new("G7", 0m, "C1")
    };

    // Test probe: counts how many source records were pulled. The production code has no counter.
    private static IEnumerable<DeliveryRequest> Counting(Action onPull)
    {
        foreach (var r in Requests)
        {
            onPull();
            yield return r;
        }
    }

    [Fact]
    public void QuoteStream_DoesNothing_UntilEnumerated()
    {
        var pulled = 0;

        var stream = Pipeline.QuoteStream(Counting(() => pulled++), 50m, Policies.Standard);
        Assert.Equal(0, pulled);

        var first = stream.First();
        Assert.Equal(1, pulled);
        Assert.Equal("A1", first.Id);
        Assert.Equal(700m, first.Price);
    }

    [Fact]
    public void QuoteStream_ChecksArgumentsImmediately()
    {
        IEnumerable<DeliveryRequest>? none = null;

        Assert.Throws<ArgumentNullException>(() => Pipeline.QuoteStream(none!, 50m, Policies.Standard));
        Assert.Throws<ArgumentNullException>(() => Pipeline.QuoteStream(Requests, 50m, null!));
    }

    [Fact]
    public void LazyQuery_RepeatedEnumeration_RepeatsWork_ButSnapshotDoesNot()
    {
        var pulled = 0;
        var stream = Pipeline.QuoteStream(Counting(() => pulled++), 50m, Policies.Standard);

        _ = stream.ToList();
        _ = stream.ToList();
        Assert.Equal(8, pulled);             // 4 records, enumerated twice

        pulled = 0;
        var snapshot = stream.ToImmutableArray();
        Assert.Equal(4, pulled);
        _ = snapshot.Sum(q => q.Price);
        _ = snapshot.Count();
        Assert.Equal(4, pulled);             // the snapshot is reused, source not read again
        Assert.Equal(3, snapshot.Length);    // D4 (70 km) filtered out
    }

    [Fact]
    public void QuoteStream_GivesSameQuotesAsEagerPipeline()
    {
        var lazy = Pipeline.QuoteStream(Requests, 50m, Policies.Express).ToList();
        var eager = Pipeline.CalculateQuotes(Pipeline.SelectServiceable(Requests, 50m), Policies.Express);

        Assert.Equal(eager.ToList(), lazy);
    }

    // ---------- resource lifetime ----------
    private sealed class TrackingReader : StringReader
    {
        public bool Disposed { get; private set; }
        public TrackingReader(string text) : base(text) { }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReader : TextReader
    {
        private int _calls;
        public bool Disposed { get; private set; }
        public override string? ReadLine()
            => _calls++ == 0 ? "A1,2" : throw new IOException("disk failure");
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void ReadLinesLazily_OpensOnlyWhenEnumerated_AndDisposesAfterFullRead()
    {
        var reader = new TrackingReader("A1,2\nB2,10\n");
        var opened = 0;

        var lines = FileLoader.ReadLinesLazily(() => { opened++; return reader; });
        Assert.Equal(0, opened);
        Assert.False(reader.Disposed);

        var snapshot = lines.ToList();

        Assert.Equal(new[] { "A1,2", "B2,10" }, snapshot);
        Assert.Equal(1, opened);
        Assert.True(reader.Disposed);
        _ = snapshot.Count;                  // reusing the snapshot does not reopen anything
        Assert.Equal(1, opened);
    }

    [Fact]
    public void ReadLinesLazily_EarlyStop_DisposesReader()
    {
        var reader = new TrackingReader("A1,2\nB2,10\nC3,50\n");

        var first = FileLoader.ReadLinesLazily(() => reader).First();

        Assert.Equal("A1,2", first);
        Assert.True(reader.Disposed);
    }

    [Fact]
    public void ReadLinesLazily_Failure_DisposesReader()
    {
        var reader = new FailingReader();

        Assert.Throws<IOException>(() => FileLoader.ReadLinesLazily(() => reader).ToList());

        Assert.True(reader.Disposed);
    }

    [Fact]
    public void FileExporter_Success_WritesFile_AndLeavesNoTempFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.csv");

        var result = FileExporter.Save(path, "a,b\n");

        Assert.IsType<Result<string>.Success>(result);
        Assert.Equal("a,b\n", File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void FileExporter_Failure_IsReported_AndNothingIsLeftBehind()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "missing_folder", "report.csv");

        var failure = Assert.IsType<Result<string>.Failure>(FileExporter.Save(path, "x"));

        Assert.Equal("EXPORT_FAILED", failure.Error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void FileExporter_ExistingReport_IsReplacedCompletely()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.csv");
        File.WriteAllText(path, "old old old old");

        FileExporter.Save(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dq_{Guid.NewGuid():N}");

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
