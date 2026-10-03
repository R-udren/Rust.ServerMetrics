using System;
using System.Linq;
using System.Text;

namespace RustServerMetrics.Config;

internal sealed class InfluxConnection
{
    public Uri WriteUri { get; }
    public string AuthorizationHeader { get; }

    private InfluxConnection(Uri writeUri, string authorizationHeader)
    {
        WriteUri = writeUri;
        AuthorizationHeader = authorizationHeader;
    }

    public static InfluxConnection Create(ConfigData config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (config.DatabaseUrl == ConfigData.DefaultInfluxDbUrl ||
            !Uri.TryCreate(config.DatabaseUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Database URL must be an absolute HTTP(S) endpoint without credentials, query, or fragment.");

        if (string.IsNullOrWhiteSpace(config.DatabaseName) ||
            config.DatabaseName == ConfigData.DefaultInfluxDBName || config.DatabaseName.Any(char.IsControl))
            throw new ArgumentException("Database name must be configured and contain no control characters.");

        if (string.IsNullOrWhiteSpace(config.DatabaseUser) || config.DatabaseUser.Contains(':') ||
            config.DatabaseUser.Any(char.IsControl) || string.IsNullOrWhiteSpace(config.DatabasePassword) ||
            config.DatabasePassword.Any(char.IsControl))
            throw new ArgumentException("Database credentials must be configured; username cannot contain a colon.");

        if (string.IsNullOrWhiteSpace(config.ServerTag) || config.ServerTag == ConfigData.DefaultServerTag ||
            config.ServerTag.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ',' || c == '=' || c == '\\'))
            throw new ArgumentException("Server tag must be configured without line-protocol delimiters.");

        if (config.BatchSize < 1000)
            throw new ArgumentException("Metrics batch size must be at least 1000.");

        var baseUri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
        var writeUri = new Uri(baseUri, "write?db=" + Uri.EscapeDataString(config.DatabaseName) + "&precision=ms");
        var credentials = Encoding.UTF8.GetBytes(config.DatabaseUser + ":" + config.DatabasePassword);
        return new InfluxConnection(writeUri, "Basic " + Convert.ToBase64String(credentials));
    }
}
