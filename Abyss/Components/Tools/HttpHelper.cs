
using System.Text;

namespace Abyss.Components.Tools;

public class HttpHelper
{
    private const int MaxHeaderCount = 100;
    private const int MaxHeaderLineLength = 8192;
    private const int MaxBodySize = 10 * 1024 * 1024; // 10 MB
    
    public static string BuildHttpResponse(
        int statusCode,
        string statusDescription,
        Dictionary<string, string>? headers = null,
        string? body = null,
        string httpVersion = "HTTP/1.1")
    {
        var responseBuilder = new StringBuilder();

        // Sanitize status description (prevent CRLF injection)
        statusDescription = SanitizeHeaderValue(statusDescription);

        responseBuilder.Append($"{httpVersion} {statusCode} {statusDescription}\r\n");

        headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Ensure correct Content-Length
        if (!string.IsNullOrEmpty(body))
        {
            int contentLength = Encoding.UTF8.GetByteCount(body);
            headers["Content-Length"] = contentLength.ToString();
            if (!headers.ContainsKey("Content-Type"))
            {
                headers["Content-Type"] = "text/plain; charset=utf-8";
            }
        }

        foreach (var header in headers)
        {
            string name = SanitizeHeaderName(header.Key);
            string value = SanitizeHeaderValue(header.Value);
            responseBuilder.AppendLine($"{name}: {value}");
        }

        responseBuilder.AppendLine();

        if (!string.IsNullOrEmpty(body))
        {
            responseBuilder.Append(body);
        }

        return responseBuilder.ToString();
    }

    public static HttpRequest Parse(string requestText)
    {
        if (string.IsNullOrEmpty(requestText))
            throw new ArgumentException("Request text cannot be empty");

        using var reader = new StringReader(requestText);
        var request = new HttpRequest();

        string requestLine = reader.ReadLine() ?? "";
        if (string.IsNullOrWhiteSpace(requestLine))
            throw new FormatException("Invalid HTTP request: missing request line");

        ParseRequestLine(requestLine, request);
        ParseHeaders(reader, request);
        ParseBody(reader, request);

        return request;
    }

    private static void ParseRequestLine(string requestLine, HttpRequest request)
    {
        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 3)
            throw new FormatException("Invalid request line format");

        request.Method = parts[0].Trim();

        if (!Uri.TryCreate(parts[1], UriKind.RelativeOrAbsolute, out var uri))
        {
            throw new FormatException("Invalid or unsupported URI");
        }
        request.RequestUri = uri;

        request.HttpVersion = parts[2].Trim();
    }

    private static void ParseHeaders(StringReader reader, HttpRequest request)
    {
        string? line;
        int headerCount = 0;

        while (!string.IsNullOrEmpty(line = reader.ReadLine()))
        {
            if (++headerCount > MaxHeaderCount)
                throw new InvalidOperationException("Too many headers");

            if (line.Length > MaxHeaderLineLength)
                throw new InvalidOperationException("Header line too long");

            int colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                throw new FormatException($"Invalid header format: {line}");

            string headerName = SanitizeHeaderName(line.Substring(0, colonIndex).Trim());
            string headerValue = SanitizeHeaderValue(line.Substring(colonIndex + 1).Trim());

            if (request.Headers.ContainsKey(headerName))
                throw new InvalidOperationException($"Duplicate header not allowed: {headerName}");

            request.Headers[headerName] = headerValue;
        }
    }

    private static void ParseBody(StringReader reader, HttpRequest request)
    {
        if (request.Headers.TryGetValue("Content-Length", out var contentLengthStr) &&
            long.TryParse(contentLengthStr, out var contentLength) &&
            contentLength > 0)
        {
            if (contentLength > MaxBodySize)
                throw new InvalidOperationException("Request body too large");

            var buffer = new char[contentLength];
            int read = reader.ReadBlock(buffer, 0, (int)contentLength);
            request.Body = new string(buffer, 0, read);
        }
        else if (request.Headers.TryGetValue("Transfer-Encoding", out var encoding) &&
                 encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Chunked transfer encoding is not supported");
        }
    }

    private static string SanitizeHeaderName(string name)
    {
        if (name.Contains("\r") || name.Contains("\n"))
            throw new FormatException("Invalid header name");
        return name;
    }

    private static string SanitizeHeaderValue(string value)
    {
        return value.Replace("\r", "").Replace("\n", "");
    }
}

public class HttpRequest
{
    public string Method { get; set; } = "";
    public Uri? RequestUri { get; set; }
    public string HttpVersion { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; }
    public string Body { get; set; } = "";
    
    public HttpRequest()
    {
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Get header value by name (case-insensitive)
    /// </summary>
    public string? GetHeader(string headerName)
    {
        return Headers.TryGetValue(headerName, out var value) ? value : null;
    }
    
    /// <summary>
    /// Check if header exists (case-insensitive)
    /// </summary>
    public bool HasHeader(string headerName)
    {
        return Headers.ContainsKey(headerName);
    }
    
    /// <summary>
    /// Convert back to HTTP request string
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        
        // Request line
        builder.AppendLine($"{Method} {RequestUri} {HttpVersion}");
        
        // Headers
        foreach (var header in Headers)
        {
            builder.AppendLine($"{header.Key}: {header.Value}");
        }
        
        // Empty line
        builder.AppendLine();
        
        // Body
        if (!string.IsNullOrEmpty(Body))
        {
            builder.Append(Body);
        }
        
        return builder.ToString();
    }
}