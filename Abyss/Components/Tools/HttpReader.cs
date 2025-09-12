using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;

namespace Abyss.Components.Tools
{
    public static class HttpReader
    {
        private const int DefaultBufferSize = 8192;
        private const int MaxHeaderBytes = 64 * 1024;      // 64 KB header max
        private const long MaxBodyBytes = 10L * 1024 * 1024; // 10 MB body max
        private const int MaxLineLength = 8 * 1024;        // 8 KB per line max

        /// <summary>
        /// Read a full HTTP message (headers + body) from a NetworkStream and return as a string.
        /// This method enforces size limits and parses chunked encoding correctly.
        /// </summary>
        public static async Task<string> ReadHttpMessageAsync(AbyssStream stream, CancellationToken cancellationToken)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Stream is not readable", nameof(stream));

            // 1) Read header bytes until CRLFCRLF or header size limit is exceeded
            var headerBuffer = new MemoryStream();
            var tmp = new byte[DefaultBufferSize];
            int headerEndIndex = -1;
            while (true)
            {
                int n = await stream.ReadAsync(tmp.AsMemory(0, tmp.Length), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                    throw new IOException("Stream closed before HTTP header was fully read.");

                headerBuffer.Write(tmp, 0, n);

                if (headerBuffer.Length > MaxHeaderBytes)
                    throw new InvalidOperationException("HTTP header exceeds maximum allowed size.");

                // search for CRLFCRLF in the accumulated bytes
                var bytes = headerBuffer.ToArray();
                headerEndIndex = IndexOfDoubleCrlf(bytes);
                if (headerEndIndex >= 0)
                {
                    // headerEndIndex is the index of the first '\r' of "\r\n\r\n"
                    // stop reading further here; remaining bytes (if any) are part of body initial chunk
                    break;
                }

                // continue reading
            }

            var allHeaderBytes = headerBuffer.ToArray();
            int bodyStartIndex = headerEndIndex + 4;
            string headerPart = Encoding.ASCII.GetString(allHeaderBytes, 0, headerEndIndex + 4);

            // 2) parse headers to find Content-Length / Transfer-Encoding
            int contentLength = 0;
            bool isChunked = false;

            foreach (var line in headerPart.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = line.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int len))
                    {
                        if (len < 0) throw new FormatException("Negative Content-Length not allowed.");
                        contentLength = len;
                    }
                    else
                    {
                        throw new FormatException("Invalid Content-Length value.");
                    }
                }
                else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isChunked = true;
                    }
                }
            }

            // 3) Create a buffered reader that first consumes bytes already read after header
            var initialTail = new ArraySegment<byte>(allHeaderBytes, bodyStartIndex, allHeaderBytes.Length - bodyStartIndex);
            var reader = new BufferedNetworkReader(stream, initialTail, DefaultBufferSize, cancellationToken);

            // 4) Read body according to encoding
            byte[] bodyBytes;
            if (isChunked)
            {
                using var bodyMs = new MemoryStream();
                while (true)
                {
                    string sizeLine = await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(sizeLine))
                    {
                        // skip empty lines (robustness)
                        continue;
                    }

                    // chunk-size [; extensions]
                    var semi = sizeLine.IndexOf(';');
                    var sizeToken = semi >= 0 ? sizeLine.Substring(0, semi) : sizeLine;
                    if (!long.TryParse(sizeToken.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long chunkSize))
                        throw new IOException("Invalid chunk size in chunked encoding.");

                    if (chunkSize < 0) throw new IOException("Negative chunk size.");

                    if (chunkSize == 0)
                    {
                        // read and discard any trailer headers until an empty line
                        while (true)
                        {
                            var trailerLine = await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false);
                            if (string.IsNullOrEmpty(trailerLine)) break;
                        }
                        break;
                    }

                    if (chunkSize > MaxBodyBytes || (bodyMs.Length + chunkSize) > MaxBodyBytes)
                        throw new InvalidOperationException("Chunked body exceeds maximum allowed size.");

                    await reader.ReadExactAsync(bodyMs, chunkSize).ConfigureAwait(false);

                    // after chunk data there must be CRLF; consume it
                    var crlf = await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false);
                    if (crlf != string.Empty)
                        throw new IOException("Missing CRLF after chunk data.");
                }

                bodyBytes = bodyMs.ToArray();
            }
            else if (contentLength > 0)
            {
                if (contentLength > MaxBodyBytes)
                    throw new InvalidOperationException("Content-Length exceeds maximum allowed size.");

                using var bodyMs = new MemoryStream();
                long remaining = contentLength;
                // If there were initial tail bytes, BufferedNetworkReader will supply them first
                await reader.ReadExactAsync(bodyMs, remaining).ConfigureAwait(false);
                bodyBytes = bodyMs.ToArray();
            }
            else
            {
                // no body
                bodyBytes = Array.Empty<byte>();
            }

            // 5) combine headerPart and body decoded as UTF-8 string
            string bodyPart = Encoding.UTF8.GetString(bodyBytes);
            return headerPart + bodyPart;
        }

        private static int IndexOfDoubleCrlf(byte[] data)
        {
            // find sequence \r\n\r\n
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// BufferedNetworkReader merges an initial buffer (already-read bytes) with later reads from NetworkStream.
        /// It provides ReadLineAsync and ReadExactAsync semantics used by HTTP parsing.
        /// </summary>
        private sealed class BufferedNetworkReader
        {
            private readonly AbyssStream _stream;
            private readonly CancellationToken _cancellation;
            private readonly int _bufferSize;
            private byte[] _buffer;
            private int _offset;
            private int _count; // valid data range [_offset, _offset + _count)

            public BufferedNetworkReader(AbyssStream stream, ArraySegment<byte> initial, int bufferSize, CancellationToken cancellation)
            {
                _stream = stream ?? throw new ArgumentNullException(nameof(stream));
                _cancellation = cancellation;
                _bufferSize = Math.Max(512, bufferSize);
                // initialize buffer and copy initial tail bytes
                _buffer = new byte[Math.Max(_bufferSize, initial.Count)];
                Array.Copy(initial.Array ?? Array.Empty<byte>(), initial.Offset, _buffer, 0, initial.Count);
                _offset = 0;
                _count = initial.Count;
            }

            /// <summary>
            /// Read a line terminated by CRLF. Returns the line without CRLF.
            /// Throws if the line length exceeds maxLineLength.
            /// </summary>
            public async Task<string> ReadLineAsync(int maxLineLength)
            {
                var ms = new MemoryStream();
                int seen = 0;
                while (true)
                {
                    if (_count == 0)
                    {
                        // refill buffer
                        int n = await _stream.ReadAsync(new Memory<byte>(_buffer, 0, _buffer.Length), _cancellation).ConfigureAwait(false);
                        if (n == 0)
                            throw new IOException("Unexpected end of stream while reading line.");
                        _offset = 0;
                        _count = n;
                    }

                    // scan for '\n'
                    int i;
                    for (i = 0; i < _count; i++)
                    {
                        byte b = _buffer[_offset + i];
                        seen++;
                        if (seen > maxLineLength) throw new InvalidOperationException("Line length exceeds maximum allowed.");
                        if (b == (byte)'\n')
                        {
                            // write bytes up to this position
                            ms.Write(_buffer, _offset, i + 1);
                            _offset += i + 1;
                            _count -= i + 1;
                            // convert to string and remove CRLF if present
                            var lineBytes = ms.ToArray();
                            if (lineBytes.Length >= 2 && lineBytes[lineBytes.Length - 2] == (byte)'\r')
                                return Encoding.ASCII.GetString(lineBytes, 0, lineBytes.Length - 2);
                            else if (lineBytes.Length >= 1 && lineBytes[lineBytes.Length - 1] == (byte)'\n')
                                return Encoding.ASCII.GetString(lineBytes, 0, lineBytes.Length - 1);
                            else
                                return Encoding.ASCII.GetString(lineBytes);
                        }
                    }

                    // no newline found in buffer; write all and continue
                    ms.Write(_buffer, _offset, _count);
                    _offset = 0;
                    _count = 0;
                }
            }

            /// <summary>
            /// Read exactly 'length' bytes and write them to destination stream.
            /// Throws if stream ends before length bytes are read or size exceeds limits.
            /// </summary>
            public async Task ReadExactAsync(Stream destination, long length)
            {
                if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
                long remaining = length;
                var tmp = new byte[_bufferSize];

                // first consume from internal buffer
                if (_count > 0)
                {
                    int take = (int)Math.Min(_count, remaining);
                    destination.Write(_buffer, _offset, take);
                    _offset += take;
                    _count -= take;
                    remaining -= take;
                }

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(tmp.Length, remaining);
                    int n = await _stream.ReadAsync(tmp.AsMemory(0, toRead), _cancellation).ConfigureAwait(false);
                    if (n == 0) throw new IOException("Unexpected end of stream while reading body.");
                    destination.Write(tmp, 0, n);
                    remaining -= n;
                }
            }
        }
    }
}
