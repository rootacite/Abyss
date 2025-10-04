using System.Net.Sockets;
using System.Text;

namespace abyssctl.Static;

public static class SocketExtensions
{
    public static async Task<string> ReadBase64Async(this Socket socket, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();

        while (true)
        {
            int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                throw new SocketException((int)SocketError.ConnectionReset);

            string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            sb.Append(chunk);

            int newlineIndex = sb.ToString().IndexOf('\n');
            if (newlineIndex >= 0)
            {
                string base64 = sb.ToString(0, newlineIndex).Trim();
                sb.Remove(0, newlineIndex + 1);
                return base64;
            }
        }
    }

    public static async Task WriteBase64Async(this Socket socket, string base64, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new ArgumentException("Base64 string cannot be null or empty.", nameof(base64));

        string message = base64 + "\n";
        byte[] data = Encoding.UTF8.GetBytes(message);

        int totalSent = 0;
        while (totalSent < data.Length)
        {
            int sent = await socket.SendAsync(data.AsMemory(totalSent), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (sent == 0)
                throw new SocketException((int)SocketError.ConnectionReset);

            totalSent += sent;
        }
    }
}