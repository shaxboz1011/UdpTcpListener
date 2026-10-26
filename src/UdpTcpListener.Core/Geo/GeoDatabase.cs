using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UdpTcpListener.Core.Geo;

/// <summary>Where a public IP address is registered.</summary>
public sealed record GeoInfo(string? CountryCode, long? Asn, string? Organization)
{
    public override string ToString()
    {
        var parts = new List<string>(2);
        if (CountryCode is not null) parts.Add(CountryCode);
        if (Asn is not null) parts.Add(Organization is null ? $"AS{Asn}" : $"AS{Asn} {Organization}");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Offline GeoIP/ASN lookup from the free DB-IP Lite CSV files (CC BY 4.0, https://db-ip.com):
/// <c>dbip-country-lite-YYYY-MM.csv[.gz]</c> (start,end,country) and
/// <c>dbip-asn-lite-YYYY-MM.csv[.gz]</c> (start,end,asn,organisation).
/// Lookups are binary searches over sorted ranges and are thread-safe once loaded.
/// </summary>
public sealed class GeoDatabase
{
    public const string Attribution = "IP geolocation by DB-IP (https://db-ip.com), CC BY 4.0";

    private readonly RangeTable<string> _countries;
    private readonly RangeTable<(long Asn, string? Org)> _asns;

    private GeoDatabase(RangeTable<string> countries, RangeTable<(long, string?)> asns)
    {
        _countries = countries;
        _asns = asns;
    }

    public static GeoDatabase Empty { get; } = new(new RangeTable<string>(), new RangeTable<(long, string?)>());

    public bool HasCountries => _countries.Count > 0;
    public bool HasAsns => _asns.Count > 0;
    public bool IsEmpty => !HasCountries && !HasAsns;
    public int RangeCount => _countries.Count + _asns.Count;

    /// <summary>Null for private, loopback, multicast and other non-public addresses, or when nothing is known.</summary>
    public GeoInfo? Lookup(IPAddress address)
    {
        if (IsEmpty || !IsPublic(address))
            return null;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var country = _countries.Find(address, out var c) ? c : null;
        var asn = _asns.Find(address, out var a) ? a : default;
        return country is null && asn.Asn == 0 ? null : new GeoInfo(country, asn.Asn == 0 ? null : asn.Asn, asn.Org);
    }

    public static GeoDatabase Load(TextReader? countryCsv, TextReader? asnCsv)
    {
        var countries = new RangeTable<string>();
        var asns = new RangeTable<(long, string?)>();
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string s) => strings.TryGetValue(s, out var existing) ? existing : strings[s] = s;

        if (countryCsv is not null)
        {
            foreach (var fields in Csv(countryCsv))
            {
                if (fields.Count >= 3 && fields[2].Length == 2 && fields[2] != "ZZ")
                    countries.Add(fields[0], fields[1], Intern(fields[2].ToUpperInvariant()));
            }
        }
        if (asnCsv is not null)
        {
            foreach (var fields in Csv(asnCsv))
            {
                if (fields.Count >= 3 && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var asn) && asn > 0)
                    asns.Add(fields[0], fields[1], (asn, fields.Count > 3 && fields[3].Length > 0 ? Intern(fields[3]) : null));
            }
        }
        countries.Seal();
        asns.Seal();
        return new GeoDatabase(countries, asns);
    }

    /// <summary>Loads the newest country and ASN files found in <paramref name="directory"/> (plain or gzipped CSV).</summary>
    public static GeoDatabase LoadDirectory(string directory)
    {
        var country = GeoFiles.Find(directory, GeoFiles.CountryPrefix);
        var asn = GeoFiles.Find(directory, GeoFiles.AsnPrefix);
        if (country is null && asn is null)
            return Empty;
        using var c = country is null ? null : GeoFiles.OpenText(country);
        using var a = asn is null ? null : GeoFiles.OpenText(asn);
        return Load(c, a);
    }

    /// <summary>True for globally routable unicast addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            return !(b[0] is 0 or 10 or 127 or >= 224
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)   // carrier-grade NAT
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // IETF, TEST-NET-1
                     || (b[0] == 198 && b[1] is 18 or 19)          // benchmarking
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal ||
                address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any))
                return false;
            Span<byte> b = stackalloc byte[16];
            address.TryWriteBytes(b, out _);
            return (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8); // 2000::/3 minus documentation
        }
        return false;
    }

    /// <summary>Minimal CSV reader: comma separated, double-quoted fields with "" escapes, one record per line.</summary>
    internal static IEnumerable<List<string>> Csv(TextReader reader)
    {
        var fields = new List<string>(4);
        var field = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            fields.Clear();
            field.Clear();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else field.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { fields.Add(field.ToString()); field.Clear(); }
                else field.Append(ch);
            }
            fields.Add(field.ToString());
            yield return fields;
        }
    }

    /// <summary>Sorted, non-overlapping address ranges; IPv4 and IPv6 are kept apart.</summary>
    private sealed class RangeTable<T>
    {
        private readonly List<(uint Start, uint End, T Value)> _v4 = [];
        private readonly List<(UInt128 Start, UInt128 End, T Value)> _v6 = [];
        private (uint Start, uint End, T Value)[] _v4Sorted = [];
        private (UInt128 Start, UInt128 End, T Value)[] _v6Sorted = [];

        public int Count => _v4Sorted.Length + _v6Sorted.Length;

        public void Add(string start, string end, T value)
        {
            if (!IPAddress.TryParse(start, out var s) || !IPAddress.TryParse(end, out var e) || s.AddressFamily != e.AddressFamily)
                return;
            if (s.AddressFamily == AddressFamily.InterNetwork)
                _v4.Add((V4(s), V4(e), value));
            else
                _v6.Add((V6(s), V6(e), value));
        }

        public void Seal()
        {
            _v4Sorted = [.. _v4.OrderBy(r => r.Start)];
            _v6Sorted = [.. _v6.OrderBy(r => r.Start)];
            _v4.Clear();
            _v6.Clear();
        }

        public bool Find(IPAddress address, out T value)
        {
            value = default!;
            return address.AddressFamily == AddressFamily.InterNetwork
                ? Search(_v4Sorted, V4(address), out value)
                : Search(_v6Sorted, V6(address), out value);
        }

        private static bool Search<TKey>((TKey Start, TKey End, T Value)[] ranges, TKey key, out T value)
            where TKey : IComparable<TKey>
        {
            // Last range whose start <= key.
            int lo = 0, hi = ranges.Length - 1, found = -1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (ranges[mid].Start.CompareTo(key) <= 0) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found >= 0 && ranges[found].End.CompareTo(key) >= 0)
            {
                value = ranges[found].Value;
                return true;
            }
            value = default!;
            return false;
        }

        private static uint V4(IPAddress a)
        {
            Span<byte> b = stackalloc byte[4];
            a.TryWriteBytes(b, out _);
            return BinaryPrimitives.ReadUInt32BigEndian(b);
        }

        private static UInt128 V6(IPAddress a)
        {
            Span<byte> b = stackalloc byte[16];
            a.TryWriteBytes(b, out _);
            return BinaryPrimitives.ReadUInt128BigEndian(b);
        }
    }
}

/// <summary>Finding, opening and downloading the DB-IP Lite files.</summary>
public static class GeoFiles
{
    public const string CountryPrefix = "dbip-country-lite";
    public const string AsnPrefix = "dbip-asn-lite";
    public const string DefaultBaseUrl = "https://download.db-ip.com/free/";

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData ? appData : Path.GetTempPath(),
            "UdpTcpListener", "geoip");

    /// <summary>Newest file with the prefix (names contain YYYY-MM, so the name order is the date order).</summary>
    public static string? Find(string directory, string prefix)
    {
        if (!Directory.Exists(directory))
            return null;
        return Directory.EnumerateFiles(directory, prefix + "*")
            .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static DateTimeOffset? LastUpdated(string directory) =>
        new[] { Find(directory, CountryPrefix), Find(directory, AsnPrefix) }
            .Where(f => f is not null)
            .Select(f => (DateTimeOffset?)File.GetLastWriteTimeUtc(f!))
            .Max();

    public static TextReader OpenText(string path)
    {
        Stream stream = File.OpenRead(path);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            stream = new GZipStream(stream, CompressionMode.Decompress);
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
    }

    /// <summary>Names DB-IP publishes for a month, e.g. dbip-country-lite-2026-10.csv.gz.</summary>
    public static string FileName(string prefix, DateTimeOffset month) =>
        $"{prefix}-{month.Year:D4}-{month.Month:D2}.csv.gz";

    /// <summary>
    /// Downloads this month's country and ASN files (falling back to last month's while the new
    /// ones are not published yet), checks that they parse, and replaces older files in <paramref name="directory"/>.
    /// </summary>
    /// <returns>The file names that were installed.</returns>
    public static async Task<IReadOnlyList<string>> DownloadAsync(
        string directory, HttpClient http, IProgress<string>? progress = null, string baseUrl = DefaultBaseUrl,
        DateTimeOffset? now = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var installed = new List<string>();
        var today = now ?? DateTimeOffset.UtcNow;
        foreach (var prefix in new[] { CountryPrefix, AsnPrefix })
        {
            HttpRequestException? lastError = null;
            string? done = null;
            foreach (var month in new[] { today, today.AddMonths(-1) })
            {
                var name = FileName(prefix, month);
                var url = baseUrl.TrimEnd('/') + "/" + name;
                progress?.Report(name);
                var temp = Path.Combine(directory, name + ".download");
                try
                {
                    using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var file = File.Create(temp);
                        await response.Content.CopyToAsync(file, ct);
                    }
                    Validate(temp);
                    var target = Path.Combine(directory, name);
                    File.Move(temp, target, overwrite: true);
                    foreach (var old in Directory.EnumerateFiles(directory, prefix + "*").Where(f => !f.Equals(target, StringComparison.Ordinal)))
                        File.Delete(old);
                    done = name;
                    break;
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }
            if (done is null)
                throw lastError ?? new HttpRequestException($"Could not download {prefix}.");
            installed.Add(done);
        }
        return installed;
    }

    /// <exception cref="InvalidDataException">The file is not a DB-IP CSV.</exception>
    private static void Validate(string path)
    {
        using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        var line = reader.ReadLine();
        if (line is null || line.Split(',').Length < 3 || !IPAddress.TryParse(line.AsSpan(0, line.IndexOf(',')), out _))
            throw new InvalidDataException($"{Path.GetFileName(path)} does not look like a DB-IP CSV file.");
    }
}
