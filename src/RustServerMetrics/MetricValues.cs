using System.Globalization;
using System.Net;
using System.Text;

namespace RustServerMetrics;

internal static class MetricValues
{
    public static long NetworkBytes(long length, int recipients) => length * recipients;

    public static bool AppendFrameMetric(StringBuilder builder, string measurement, string serverTag,
        string epoch, float instant, float average)
    {
        if (float.IsNaN(instant) || float.IsInfinity(instant) ||
            float.IsNaN(average) || float.IsInfinity(average))
            return false;

        builder.Append(measurement).Append(",server=").Append(serverTag)
            .Append(" instant=").Append(instant.ToString(CultureInfo.InvariantCulture))
            .Append(",average=").Append(average.ToString(CultureInfo.InvariantCulture))
            .Append(' ').Append(epoch).Append('\n');
        return true;
    }

    public static void AppendConnectionTags(StringBuilder builder, string steamId, string address)
    {
        builder.Append(",steamid=").Append(steamId);
        var host = AddressHost(address);
        if (host.Length > 0)
            builder.Append(",ip=").Append(host);
    }

    public static string AddressHost(string address)
    {
        if (string.IsNullOrEmpty(address))
            return string.Empty;
        if (IPAddress.TryParse(address, out var parsed))
            return parsed.ToString();

        var separator = address.LastIndexOf(':');
        if (separator < 0)
            return address;

        return address[..separator].Trim('[', ']');
    }
}
