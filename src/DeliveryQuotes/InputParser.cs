using System.Collections.Immutable;
using System.Globalization;

namespace DeliveryQuotes;

public static class InputParser
{
    // Line format: id,customerId,distanceKm
    public static bool TryParseLine(string? line, out DeliveryRequest? request, out string error)
    {
        request = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "Line is missing or empty.";
            return false;
        }

        var parts = line.Split(',');
        if (parts.Length != 3)
        {
            error = $"Expected 'id,customerId,distanceKm' but got '{line}'.";
            return false;
        }

        var id = parts[0].Trim();
        if (id.Length == 0)
        {
            error = $"Id is empty in '{line}'.";
            return false;
        }

        var customerId = parts[1].Trim();
        if (customerId.Length == 0)
        {
            error = $"Customer id is empty in '{line}'.";
            return false;
        }

        if (!decimal.TryParse(parts[2].Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var km))
        {
            error = $"Distance '{parts[2].Trim()}' is not a number (id {id}).";
            return false;
        }

        if (km < 0)
        {
            error = $"Distance must be >= 0 but was {km} (id {id}).";
            return false;
        }

        request = new DeliveryRequest(id, km, customerId);
        error = string.Empty;
        return true;
    }

    // null = missing input (error). Empty collection = valid empty dataset.
    public static ParseOutcome ParseAll(IEnumerable<string>? lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var valid = ImmutableArray.CreateBuilder<DeliveryRequest>();
        var errors = ImmutableArray.CreateBuilder<string>();

        foreach (var line in lines)
        {
            if (TryParseLine(line, out var request, out var error) && request is not null)
                valid.Add(request);
            else
                errors.Add(error);
        }

        return new ParseOutcome(valid.ToImmutable(), errors.ToImmutable());
    }
}

// Line format: id,name,tier,creditLimit   (tier: regular | premium)
public static class CustomerParser
{
    public static bool TryParseLine(string? line, out Customer? customer, out string error)
    {
        customer = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "Line is missing or empty.";
            return false;
        }

        var parts = line.Split(',');
        if (parts.Length != 4)
        {
            error = $"Expected 'id,name,tier,creditLimit' but got '{line}'.";
            return false;
        }

        var id = parts[0].Trim();
        var name = parts[1].Trim();
        if (id.Length == 0)
        {
            error = $"Customer id is empty in '{line}'.";
            return false;
        }

        if (name.Length == 0)
        {
            error = $"Name is empty (customer {id}).";
            return false;
        }

        if (!Enum.TryParse<CustomerTier>(parts[2].Trim(), true, out var tier) || !Enum.IsDefined(tier))
        {
            error = $"Tier '{parts[2].Trim()}' is not valid (customer {id}); use regular or premium.";
            return false;
        }

        if (!decimal.TryParse(parts[3].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var credit))
        {
            error = $"Credit limit '{parts[3].Trim()}' is not a number (customer {id}).";
            return false;
        }

        if (credit < 0)
        {
            error = $"Credit limit must be >= 0 but was {credit} (customer {id}).";
            return false;
        }

        customer = new Customer(id, name, tier, credit);
        error = string.Empty;
        return true;
    }

    public static (ImmutableArray<Customer> Valid, ImmutableArray<string> Errors) ParseAll(IEnumerable<string>? lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var valid = ImmutableArray.CreateBuilder<Customer>();
        var errors = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (!TryParseLine(line, out var customer, out var error) || customer is null)
            {
                errors.Add(error);
                continue;
            }

            if (!seen.Add(customer.Id))
            {
                errors.Add($"Duplicate customer id '{customer.Id}'.");
                continue;
            }

            valid.Add(customer);
        }

        return (valid.ToImmutable(), errors.ToImmutable());
    }
}

// Boundary: the ONLY place that reads files and catches I/O exceptions.
public static class FileLoader
{
    // Streaming reader. The reader is opened lazily (on the first MoveNext) and OWNED by this iterator:
    // `using` releases it when enumeration finishes, fails, or is stopped early (enumerator Dispose).
    public static IEnumerable<string> ReadLinesLazily(Func<TextReader> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return Iterate(open);

        static IEnumerable<string> Iterate(Func<TextReader> opener)
        {
            using var reader = opener();
            while (reader.ReadLine() is { } line)
                yield return line;
        }
    }

    // Reads the file inside a bounded scope (ToArray) and returns a snapshot of the non-blank lines.
    private static Result<string[]> ReadDataLines(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<string[]>.Fail(new DomainError("FILE_NOT_FOUND", "No file path was given."));

        string file = path;
        try
        {
            var lines = ReadLinesLazily(() => new StreamReader(file))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            return Result<string[]>.Ok(lines);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result<string[]>.Fail(new DomainError("FILE_NOT_FOUND", $"File '{path}' was not found."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string[]>.Fail(new DomainError("FILE_READ_ERROR", $"Cannot read file '{path}': {ex.Message}"));
        }
    }

    // Strict, all-or-nothing: any malformed line makes the whole load fail (no partial data).
    // An empty file is a valid empty dataset.
    public static Result<ImmutableArray<DeliveryRequest>> LoadRequests(string? path)
        => ReadDataLines(path).Bind(lines =>
        {
            var parsed = InputParser.ParseAll(lines);
            return parsed.Errors.Length == 0
                ? Result<ImmutableArray<DeliveryRequest>>.Ok(parsed.Valid)
                : Result<ImmutableArray<DeliveryRequest>>.Fail(new DomainError(
                    "MALFORMED_CONTENT",
                    $"File '{path}' has {parsed.Errors.Length} malformed line(s): {string.Join(" | ", parsed.Errors)}"));
        });

    public static Result<ImmutableArray<Customer>> LoadCustomers(string? path)
        => ReadDataLines(path).Bind(lines =>
        {
            var (valid, errors) = CustomerParser.ParseAll(lines);
            return errors.Length == 0
                ? Result<ImmutableArray<Customer>>.Ok(valid)
                : Result<ImmutableArray<Customer>>.Fail(new DomainError(
                    "MALFORMED_CONTENT",
                    $"File '{path}' has {errors.Length} invalid customer line(s): {string.Join(" | ", errors)}"));
        });
}

// Boundary: writes a report. Writes to a temporary file first, then moves it over the target,
// so a failure never leaves a half-written report.
public static class FileExporter
{
    public static Result<string> Save(string? path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(path))
            return Result<string>.Fail(new DomainError("EXPORT_FAILED", "No output path was given."));

        var temp = path + ".tmp";
        try
        {
            // Owner: this method. Released at the end of the using block, also when Write throws.
            using (var writer = new StreamWriter(temp, false, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(content);
            }
            File.Move(temp, path, overwrite: true);
            return Result<string>.Ok(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail(new DomainError("EXPORT_FAILED", $"Cannot write '{path}': {ex.Message}"));
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
