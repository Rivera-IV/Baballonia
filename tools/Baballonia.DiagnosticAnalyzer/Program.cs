using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: Baballonia.DiagnosticAnalyzer <report.zip|extracted-directory> [output-directory]");
    return 2;
}

try
{
    var inputPath = Path.GetFullPath(args[0]);
    var outputPath = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            Path.GetFileNameWithoutExtension(inputPath) + ".analysis");

    if (Directory.Exists(outputPath) || File.Exists(outputPath))
        throw new IOException($"Refusing to replace an existing analysis output: {outputPath}");

    Directory.CreateDirectory(outputPath);
    try
    {
        var sourcePath = PrepareSource(inputPath, outputPath);
        var result = DiagnosticAnalyzer.Analyze(sourcePath, outputPath);
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        File.WriteAllText(
            Path.Combine(outputPath, "analysis.json"),
            JsonSerializer.Serialize(result, jsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(outputPath, "analysis.md"),
            DiagnosticAnalyzer.ToMarkdown(result),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(outputPath, "LOCAL_AI_PROMPT.md"),
            DiagnosticAnalyzer.CreateAiPrompt(result),
            new UTF8Encoding(false));

        Console.WriteLine($"Diagnostic analysis: {Path.Combine(outputPath, "analysis.json")}");
        Console.WriteLine($"Human-readable report: {Path.Combine(outputPath, "analysis.md")}");
        Console.WriteLine($"Selected evidence: {Path.Combine(outputPath, "evidence")}");
        return result.Findings.Any(finding => finding.Severity == "error") ? 3 : 0;
    }
    catch
    {
        Directory.Delete(outputPath, recursive: true);
        throw;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static string PrepareSource(string inputPath, string outputPath)
{
    if (Directory.Exists(inputPath))
        return inputPath;
    if (!File.Exists(inputPath))
        throw new FileNotFoundException("Diagnostic report was not found.", inputPath);
    if (!string.Equals(Path.GetExtension(inputPath), ".zip", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The analyzer accepts a decrypted ZIP or an extracted directory.");

    var sourcePath = Path.Combine(outputPath, "source");
    Directory.CreateDirectory(sourcePath);
    var sourceRoot = Path.GetFullPath(sourcePath) + Path.DirectorySeparatorChar;
    using var archive = ZipFile.OpenRead(inputPath);
    foreach (var entry in archive.Entries)
    {
        var destination = Path.GetFullPath(Path.Combine(sourcePath, entry.FullName));
        if (!destination.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe path in diagnostic ZIP: {entry.FullName}");
        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destination);
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: false);
    }
    return sourcePath;
}

internal static class DiagnosticAnalyzer
{
    private const double PupilMissingThreshold = 0.03;
    private const double MaximumNormalBinocularDifference = 0.08;

    public static AnalysisResult Analyze(string sourcePath, string outputPath)
    {
        var csvPath = Path.Combine(sourcePath, "eye_pipeline.csv");
        var metadataPath = Path.Combine(sourcePath, "metadata.json");
        if (!File.Exists(csvPath)) throw new InvalidDataException("eye_pipeline.csv is missing.");
        if (!File.Exists(metadataPath)) throw new InvalidDataException("metadata.json is missing.");

        using var metadataDocument = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var metadata = metadataDocument.RootElement;
        var table = CsvTable.Read(csvPath);
        if (table.Rows.Count < 2) throw new InvalidDataException("The diagnostic contains too few frames.");

        var rows = table.Rows;
        var times = Values(rows, "time_ms");
        var intervals = times.Zip(times.Skip(1), (left, right) => right - left).Where(value => value >= 0).ToArray();
        var durationMs = Math.Max(times[^1] - times[0], 0.001);
        var fps = (rows.Count - 1) * 1000.0 / durationMs;
        var stateCounts = rows
            .GroupBy(row => Text(row, "filtered_state", "unknown"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var findings = new List<Finding>();
        var events = new List<DiagnosticEvent>();

        var longGaps = intervals
            .Select((gap, index) => (Gap: gap, Sequence: Sequence(rows[index + 1], index + 1)))
            .Where(item => item.Gap > 100)
            .OrderByDescending(item => item.Gap)
            .ToArray();
        if (longGaps.Length > 0)
        {
            findings.Add(new Finding(
                "pipeline_stalls", "warning",
                $"Detected {longGaps.Length} frame gaps above 100 ms; the longest was {longGaps[0].Gap:F1} ms.",
                longGaps.Select(item => item.Sequence).Take(8).ToArray()));
            events.AddRange(longGaps.Take(8).Select(item => new DiagnosticEvent(
                item.Sequence, "pipeline_gap", "warning", item.Gap,
                $"Frame interval {item.Gap:F1} ms")));
        }

        var disparityEvents = rows.Select((row, index) =>
        {
            var left = Number(row, "avatar/leftEyeX");
            var right = Number(row, "avatar/rightEyeX");
            return new
            {
                Row = row,
                Index = index,
                Sequence = Sequence(row, index),
                Difference = left - right,
                State = Text(row, "filtered_state", "unknown"),
                Convergence = Flag(row, "filtered_convergence_active"),
            };
        }).Where(item =>
            item.State == "Open" && !item.Convergence &&
            Math.Abs(item.Difference) > MaximumNormalBinocularDifference + 0.001)
          .OrderByDescending(item => Math.Abs(item.Difference))
          .ToArray();
        if (disparityEvents.Length > 0)
        {
            var worst = disparityEvents[0];
            findings.Add(new Finding(
                "open_eye_binocular_disparity", "error",
                $"The normal-gaze binocular limit was exceeded in {disparityEvents.Length} frames. " +
                $"The largest difference was {worst.Difference:F3} at frame {worst.Sequence}.",
                disparityEvents.Select(item => item.Sequence).Take(12).ToArray()));
            events.AddRange(disparityEvents.Take(12).Select(item => new DiagnosticEvent(
                item.Sequence, "binocular_disparity", "error", Math.Abs(item.Difference),
                $"Open-state left/right X difference {item.Difference:F3}")));
        }

        var pupilLoss = new Dictionary<string, int>();
        foreach (var eye in new[] { "left", "right" })
        {
            var visibilityColumn = $"model_raw/{eye}PupilVisibility";
            var lidColumn = $"avatar/{eye}EyeLid";
            var count = rows.Count(row => Number(row, visibilityColumn) <= PupilMissingThreshold &&
                                          Number(row, lidColumn) >= 0.6);
            pupilLoss[eye] = count;
            if (count > rows.Count * 0.25)
            {
                findings.Add(new Finding(
                    $"{eye}_pupil_visibility", "warning",
                    $"The {eye} pupil visibility output was missing while the lid was open in " +
                    $"{count} of {rows.Count} frames ({Percent(count, rows.Count):F1}%).",
                    rows.Select((row, index) => (row, index))
                        .Where(item => Number(item.row, visibilityColumn) <= PupilMissingThreshold &&
                                       Number(item.row, lidColumn) >= 0.6)
                        .OrderBy(item => Number(item.row, visibilityColumn))
                        .Select(item => Sequence(item.row, item.index)).Take(8).ToArray()));
            }
        }

        var registration = new Dictionary<string, RegistrationSummary>();
        foreach (var eye in new[] { "left", "right" })
        {
            var tx = Values(rows, $"registration_{eye}_tx");
            var ty = Values(rows, $"registration_{eye}_ty");
            var rotation = Values(rows, $"registration_{eye}_rotation");
            var confidence = Values(rows, $"registration_{eye}_confidence");
            registration[eye] = new RegistrationSummary(
                Min(tx), Max(tx), Min(ty), Max(ty), Min(rotation), Max(rotation),
                Mean(confidence), Percentile(confidence, 0.05));
            if (confidence.Count(value => value < 0.8) > rows.Count * 0.1)
            {
                findings.Add(new Finding(
                    $"{eye}_registration_confidence", "warning",
                    $"The {eye} registration confidence was below 0.8 in more than 10% of frames.",
                    []));
            }
        }

        var expressionStats = new Dictionary<string, ValueSummary>();
        foreach (var name in new[]
                 {
                     "avatar/leftEyeLid", "avatar/rightEyeLid",
                     "avatar/leftEyeWiden", "avatar/rightEyeWiden",
                     "avatar/leftEyeSquint", "avatar/rightEyeSquint",
                     "avatar/leftEyeBrow", "avatar/rightEyeBrow",
                     "avatar/eyeConvergence",
                 })
        {
            var values = Values(rows, name);
            expressionStats[name] = Summarize(values);
        }

        var browDifference = rows.Select(row =>
            Math.Abs(Number(row, "avatar/leftEyeBrow") - Number(row, "avatar/rightEyeBrow"))).ToArray();
        if (Percentile(browDifference, 0.95) > 0.2)
        {
            findings.Add(new Finding(
                "eyebrow_asymmetry", "info",
                $"The 95th-percentile left/right eyebrow difference was {Percentile(browDifference, 0.95):F3}. " +
                "This is only a model issue if the tester moved both eyebrows symmetrically.",
                []));
        }

        var convergenceFrames = rows.Count(row => Flag(row, "filtered_convergence_active"));
        if (convergenceFrames == 0)
        {
            findings.Add(new Finding(
                "convergence_not_observed", "info",
                "No confirmed convergence/cross-eye state was observed, so this recording cannot validate convergence.",
                []));
        }

        var confirmedWinkFrames = stateCounts
            .Where(pair => pair.Key is "LeftWink" or "RightWink")
            .Sum(pair => pair.Value);
        if (confirmedWinkFrames == 0)
        {
            findings.Add(new Finding(
                "wink_not_observed", "info",
                "No confirmed left or right wink was observed, so this recording cannot validate held winks.",
                []));
        }

        var evidence = CopyEvidence(sourcePath, outputPath, events);
        var appVersion = metadata.TryGetProperty("applicationVersion", out var versionElement)
            ? versionElement.GetString() ?? "unknown"
            : "unknown";
        var model = metadata.TryGetProperty("eyeModelFileName", out var modelElement)
            ? modelElement.GetString() ?? "unknown"
            : "unknown";

        return new AnalysisResult(
            1,
            DateTimeOffset.UtcNow,
            appVersion,
            model,
            rows.Count,
            durationMs,
            fps,
            Summarize(intervals),
            stateCounts,
            pupilLoss,
            registration,
            expressionStats,
            findings,
            events.OrderBy(item => item.Sequence).ToArray(),
            evidence);
    }

    private static IReadOnlyList<EvidenceItem> CopyEvidence(
        string sourcePath,
        string outputPath,
        IEnumerable<DiagnosticEvent> events)
    {
        var evidenceDirectory = Path.Combine(outputPath, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var selected = events
            .OrderByDescending(item => SeverityRank(item.Severity))
            .ThenByDescending(item => item.Score)
            .SelectMany(item => Enumerable.Range(Math.Max(0, item.Sequence - 1), 3)
                .Select(sequence => (Event: item, Sequence: sequence)))
            .GroupBy(item => item.Sequence)
            .Select(group => group.First())
            .Take(24)
            .OrderBy(item => item.Sequence)
            .ToArray();

        var result = new List<EvidenceItem>();
        foreach (var item in selected)
        {
            var copied = new List<string>();
            foreach (var eye in new[] { "left", "right" })
            {
                var name = $"{eye}_{item.Sequence:D6}.png";
                var source = Path.Combine(sourcePath, "frames", name);
                if (!File.Exists(source)) continue;
                var destination = Path.Combine(evidenceDirectory, name);
                File.Copy(source, destination, overwrite: false);
                copied.Add(Path.GetRelativePath(outputPath, destination).Replace('\\', '/'));
            }
            if (copied.Count > 0)
                result.Add(new EvidenceItem(item.Sequence, item.Event.Type, copied));
        }
        return result;
    }

    public static string ToMarkdown(AnalysisResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Baballonia diagnostic analysis");
        builder.AppendLine();
        builder.AppendLine($"- Application: `{result.ApplicationVersion}`");
        builder.AppendLine($"- Model: `{result.EyeModel}`");
        builder.AppendLine($"- Frames: {result.FrameCount}");
        builder.AppendLine($"- Duration: {result.DurationMs / 1000:F2} seconds");
        builder.AppendLine($"- Effective frame rate: {result.FramesPerSecond:F2} FPS");
        builder.AppendLine($"- Median interval: {result.FrameIntervals.Median:F2} ms");
        builder.AppendLine($"- 95th percentile interval: {result.FrameIntervals.P95:F2} ms");
        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in result.Findings)
        {
            builder.AppendLine($"- **{finding.Severity.ToUpperInvariant()} — {finding.Code}:** {finding.Message}");
            if (finding.Sequences.Count > 0)
                builder.AppendLine($"  Frames: {string.Join(", ", finding.Sequences)}");
        }
        builder.AppendLine();
        builder.AppendLine("## Temporal states");
        builder.AppendLine();
        foreach (var state in result.StateCounts.OrderByDescending(item => item.Value))
            builder.AppendLine($"- {state.Key}: {state.Value} ({Percent(state.Value, result.FrameCount):F1}%)");
        builder.AppendLine();
        builder.AppendLine("## Selected evidence");
        builder.AppendLine();
        foreach (var item in result.Evidence)
            builder.AppendLine($"- Frame {item.Sequence} ({item.Reason}): {string.Join(", ", item.Files.Select(file => $"`{file}`"))}");
        return builder.ToString();
    }

    public static string CreateAiPrompt(AnalysisResult result) => $$"""
        # Baballonia local diagnostic review

        Review `analysis.json` and the paired eye images under `evidence/`.

        Goals:
        1. Separate camera/image-quality problems from model-output and temporal-processing problems.
        2. For every conclusion, cite the exact frame sequence and CSV/output field that supports it.
        3. Do not infer a blink, wink, convergence, or symmetric eyebrow gesture unless the data supports it.
        4. Flag contradictions between nearly unchanged images and discontinuous avatar output.
        5. Produce a concise diagnosis with confidence levels and recommended code locations to inspect.

        The deterministic analyzer found {{result.Findings.Count}} findings and selected
        {{result.Evidence.Count}} evidence windows. Treat these findings as leads, not unquestionable truth.
        Never upload the decrypted report or eye images. Do not modify source code in diagnostic mode.
        """ + Environment.NewLine;

    private static ValueSummary Summarize(IReadOnlyList<double> values) => new(
        Min(values), Max(values), Mean(values), Percentile(values, 0.5), Percentile(values, 0.95));

    private static double[] Values(IReadOnlyList<Dictionary<string, string>> rows, string name) =>
        rows.Select(row => Number(row, name)).Where(double.IsFinite).ToArray();

    private static int Sequence(Dictionary<string, string> row, int fallback) =>
        int.TryParse(Text(row, "sequence", fallback.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string Text(Dictionary<string, string> row, string name, string fallback = "") =>
        row.TryGetValue(name, out var value) ? value : fallback;

    private static double Number(Dictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var text) &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;

    private static bool Flag(Dictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var text) && (text == "1" || bool.TryParse(text, out var value) && value);

    private static double Min(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.Min();
    private static double Max(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.Max();
    private static double Mean(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.Average();
    private static double Percent(int count, int total) => total == 0 ? 0 : count * 100.0 / total;

    private static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(value => value).ToArray();
        var position = Math.Clamp(fraction, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "error" => 3,
        "warning" => 2,
        _ => 1,
    };
}

internal static class CsvTable
{
    public static CsvData Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("CSV header is missing.");
        var headers = ParseLine(headerLine);
        if (headers.Count == 0 || headers.Distinct(StringComparer.Ordinal).Count() != headers.Count)
            throw new InvalidDataException("CSV contains missing or duplicate headers.");

        var rows = new List<Dictionary<string, string>>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = ParseLine(line);
            if (values.Count != headers.Count)
                throw new InvalidDataException($"CSV row {rows.Count + 2} has {values.Count} values; expected {headers.Count}.");
            var row = new Dictionary<string, string>(headers.Count, StringComparer.Ordinal);
            for (var index = 0; index < headers.Count; index++) row[headers[index]] = values[index];
            rows.Add(row);
        }
        return new CsvData(headers, rows);
    }

    private static List<string> ParseLine(string line)
    {
        var result = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                result.Add(field.ToString());
                field.Clear();
            }
            else field.Append(character);
        }
        if (quoted) throw new InvalidDataException("CSV contains an unterminated quoted field.");
        result.Add(field.ToString());
        return result;
    }
}

internal sealed record CsvData(
    IReadOnlyList<string> Headers,
    IReadOnlyList<Dictionary<string, string>> Rows);

internal sealed record AnalysisResult(
    int SchemaVersion,
    DateTimeOffset AnalyzedUtc,
    string ApplicationVersion,
    string EyeModel,
    int FrameCount,
    double DurationMs,
    double FramesPerSecond,
    ValueSummary FrameIntervals,
    IReadOnlyDictionary<string, int> StateCounts,
    IReadOnlyDictionary<string, int> OpenLidPupilLossFrames,
    IReadOnlyDictionary<string, RegistrationSummary> Registration,
    IReadOnlyDictionary<string, ValueSummary> Expressions,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<DiagnosticEvent> Events,
    IReadOnlyList<EvidenceItem> Evidence);

internal sealed record ValueSummary(double Min, double Max, double Mean, double Median, double P95);

internal sealed record RegistrationSummary(
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    double MinRotation,
    double MaxRotation,
    double MeanConfidence,
    double P05Confidence);

internal sealed record Finding(string Code, string Severity, string Message, IReadOnlyList<int> Sequences);

internal sealed record DiagnosticEvent(int Sequence, string Type, string Severity, double Score, string Message);

internal sealed record EvidenceItem(int Sequence, string Reason, IReadOnlyList<string> Files);
