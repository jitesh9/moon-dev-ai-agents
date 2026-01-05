/*
 * Historical Data Loader
 * Loads and parses historical bar data from CSV/JSON files for simulation
 */

using System.Globalization;

namespace GCTradingApp;

/// <summary>
/// Loads historical bar data from CSV or JSON files
/// </summary>
public class HistoricalDataLoader
{
    /// <summary>
    /// Load historical data from a CSV file
    /// Expected format: Time,Open,High,Low,Close,Volume (header row optional)
    /// </summary>
    public static List<BarData> LoadFromCsv(string filePath)
    {
        var bars = new List<BarData>();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Historical data file not found: {filePath}");
        }

        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("Historical data file is empty");
        }

        // Check if first line is header
        int startIndex = 0;
        if (lines[0].StartsWith("Time", StringComparison.OrdinalIgnoreCase) ||
            lines[0].StartsWith("Date", StringComparison.OrdinalIgnoreCase))
        {
            startIndex = 1;
        }

        for (int i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var bar = ParseCsvLine(line);
                if (bar != null)
                {
                    bars.Add(bar);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to parse line {i + 1} in {filePath}: {ex.Message}");
                // Continue processing other lines
            }
        }

        Logger.Info($"Loaded {bars.Count} bars from {filePath}");
        return bars;
    }

    /// <summary>
    /// Parse a single CSV line into BarData
    /// Expected format: Time,Open,High,Low,Close,Volume
    /// </summary>
    private static BarData? ParseCsvLine(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 5)
        {
            return null;
        }

        // Parse time (try multiple formats)
        if (!DateTime.TryParse(parts[0].Trim(), out var time))
        {
            // Try common formats
            var formats = new[]
            {
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm",
                "MM/dd/yyyy HH:mm:ss",
                "MM/dd/yyyy HH:mm",
                "yyyy-MM-dd"
            };

            bool parsed = false;
            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(parts[0].Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
                {
                    parsed = true;
                    break;
                }
            }

            if (!parsed)
            {
                return null;
            }
        }

        // Parse OHLC
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var open) ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var high) ||
            !double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var low) ||
            !double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var close))
        {
            return null;
        }

        // Parse volume (optional)
        decimal volume = 0;
        if (parts.Length > 5)
        {
            decimal.TryParse(parts[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out volume);
        }

        // Validate OHLC
        if (high < low || high < open || high < close || low > open || low > close)
        {
            Logger.Warn($"Invalid OHLC data in line: {line}");
            return null;
        }

        return new BarData
        {
            Time = time,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            WAP = (decimal)((open + high + low + close) / 4.0), // Calculate WAP from OHLC
            Count = 0
        };
    }

    /// <summary>
    /// Load historical data from a JSON file
    /// Expected format: Array of objects with Time, Open, High, Low, Close, Volume properties
    /// </summary>
    public static List<BarData> LoadFromJson(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Historical data file not found: {filePath}");
        }

        var json = File.ReadAllText(filePath);
        var bars = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BarData>>(json);

        if (bars == null)
        {
            throw new InvalidDataException("Failed to deserialize JSON data");
        }

        Logger.Info($"Loaded {bars.Count} bars from {filePath}");
        return bars;
    }

    /// <summary>
    /// Detect file format and load accordingly
    /// </summary>
    public static List<BarData> LoadFromFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        
        return extension switch
        {
            ".csv" => LoadFromCsv(filePath),
            ".json" => LoadFromJson(filePath),
            _ => throw new NotSupportedException($"Unsupported file format: {extension}. Supported formats: .csv, .json")
        };
    }
}

