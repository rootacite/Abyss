// Target: .NET 9
// NuGet: NSec.Cryptography (for X25519)
// Note: ChaCha20Poly1305 is used from System.Security.Cryptography (available in .NET 7+ / .NET 9)

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Abyss.Components.Services;
using Microsoft.AspNetCore.Authentication;
using NSec.Cryptography;

using ChaCha20Poly1305 = System.Security.Cryptography.ChaCha20Poly1305;

namespace Abyss.Components.Tools
{
    // TODO: (complete) Since C25519 has already been used for user authentication,
    // TODO: (complete) why not use that public key to verify user identity when establishing a secure channel here?
    public sealed class AbyssStream : NetworkStream, IDisposable
    {
        private const int PublicKeyLength = 32;
        private const int AeadKeyLen = 32;
        private const int NonceSaltLen = 4;
        private const int AeadTagLen = 16;
        private const int NonceLen = 12; // 4-byte salt + 8-byte counter
        private const int MaxPlaintextFrame = 64 * 1024; // 64 KiB per frame

        private readonly ChaCha20Poly1305 _aead;
        private readonly byte[] _sendNonceSalt = new byte[NonceSaltLen];
        private readonly byte[] _recvNonceSalt = new byte[NonceSaltLen];

        // Counters and locks
        private ulong _sendCounter;
        private ulong _recvCounter;
        private readonly object _sendLock = new();
        private readonly object _aeadLock = new();

        // Inbound leftover cache (FIFO)
        private readonly ConcurrentQueue<byte[]> _leftoverQueue = new();
        private byte[]? _currentLeftoverSegment;
        private int _currentLeftoverOffset;

        private bool _disposed;

        private AbyssStream(Socket socket, bool ownsSocket, byte[] aeadKey, byte[] sendSalt, byte[] recvSalt)
            : base(socket, ownsSocket)
        {
            if (aeadKey == null || aeadKey.Length != AeadKeyLen) throw new ArgumentException(nameof(aeadKey));
            if (sendSalt == null || sendSalt.Length != NonceSaltLen) throw new ArgumentException(nameof(sendSalt));
            if (recvSalt == null || recvSalt.Length != NonceSaltLen) throw new ArgumentException(nameof(recvSalt));

            Array.Copy(sendSalt, 0, _sendNonceSalt, 0, NonceSaltLen);
            Array.Copy(recvSalt, 0, _recvNonceSalt, 0, NonceSaltLen);

            // ChaCha20Poly1305 is in System.Security.Cryptography in .NET 9
            _aead = new ChaCha20Poly1305(aeadKey);
        }

        /// <summary>
        /// Create an AbyssStream over an established TcpClient.
        /// Handshake: X25519 public exchange (raw) -> shared secret -> HKDF -> AEAD key + saltA + saltB
        /// send/recv salts are assigned deterministically by lexicographic comparison of raw public keys.
        /// </summary>
        public static async Task<AbyssStream> CreateAsync(TcpClient client, UserService us, byte[]? privateKeyRaw = null, CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            var socket = client.Client ?? throw new ArgumentException("TcpClient has no underlying socket");

            // 1) Prepare local X25519 key (NSec)
            Key? localKey = null;
            try
            {
                if (privateKeyRaw != null)
                {
                    if (privateKeyRaw.Length != KeyAgreementAlgorithm.X25519.PrivateKeySize)
                        throw new ArgumentException($"privateKeyRaw must be {KeyAgreementAlgorithm.X25519.PrivateKeySize} bytes");
                    localKey = Key.Import(KeyAgreementAlgorithm.X25519, privateKeyRaw, KeyBlobFormat.RawPrivateKey);
                }
                else
                {
                    var creationParams = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
                    localKey = Key.Create(KeyAgreementAlgorithm.X25519, creationParams);
                }
            }
            catch
            {
                localKey?.Dispose();
                throw;
            }

            var localPublic = localKey.Export(KeyBlobFormat.RawPublicKey);

            // 2) Exchange public keys using raw socket APIs
            var remotePublic = new byte[PublicKeyLength];

            var sent = 0;
            while (sent < PublicKeyLength)
            {
                var toSend = new ReadOnlyMemory<byte>(localPublic, sent, PublicKeyLength - sent);
                sent += await socket.SendAsync(toSend, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }

            await ReadExactFromSocketAsync(socket, remotePublic, 0, PublicKeyLength, cancellationToken).ConfigureAwait(false);

            var ch = Encoding.UTF8.GetBytes(UserService.GenerateRandomAsciiString(32));
            sent = 0;
            while (sent < ch.Length)
            {
                var toSend = new ReadOnlyMemory<byte>(ch, sent, ch.Length - sent);
                sent += await socket.SendAsync(toSend, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            
            var rch = new byte[64];
            await ReadExactFromSocketAsync(socket, rch, 0, 64, cancellationToken).ConfigureAwait(false);
            bool rau = await us.VerifyAny(ch, rch);
            if (!rau) throw new AuthenticationFailureException("");
            
            var ack = Encoding.UTF8.GetBytes(UserService.GenerateRandomAsciiString(16));
            sent = 0;
            while (sent < ack.Length)
            {
                var toSend = new ReadOnlyMemory<byte>(ack, sent, ack.Length - sent);
                sent += await socket.SendAsync(toSend, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }

            // 3) Compute shared secret (X25519)
            PublicKey remotePub;
            try
            {
                remotePub = PublicKey.Import(KeyAgreementAlgorithm.X25519, remotePublic, KeyBlobFormat.RawPublicKey);
            }
            catch (Exception ex)
            {
                localKey.Dispose();
                throw new InvalidOperationException("Failed to import remote public key", ex);
            }

            byte[] aeadKey;
            byte[] saltA;
            byte[] saltB;

            using (var shared = KeyAgreementAlgorithm.X25519.Agree(localKey, remotePub))
            {
                if (shared == null)
                    throw new InvalidOperationException("Failed to agree remote public key");

                // Derive AEAD key and two independent nonce salts directly from the SharedSecret,
                // using HKDF-SHA256 within NSec (no raw shared-secret export).
                aeadKey = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(
                    shared,
                    salt: null,
                    info: System.Text.Encoding.ASCII.GetBytes("Abyss-AEAD-Key"),
                    count: AeadKeyLen);

                saltA = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(
                    shared,
                    salt: null,
                    info: System.Text.Encoding.ASCII.GetBytes("Abyss-Nonce-Salt-A"),
                    count: NonceSaltLen);

                saltB = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(
                    shared,
                    salt: null,
                    info: System.Text.Encoding.ASCII.GetBytes("Abyss-Nonce-Salt-B"),
                    count: NonceSaltLen);
            }

// localKey no longer needed
            localKey.Dispose();

// Deterministic assignment by lexicographic comparison of raw public keys
            byte[] sendSalt, recvSalt;
            int cmp = CompareByteArrayLexicographic(localPublic, remotePublic);
            if (cmp < 0)
            {
                sendSalt = saltA;
                recvSalt = saltB;
            }
            else if (cmp > 0)
            {
                sendSalt = saltB;
                recvSalt = saltA;
            }
            else
            {
                // extremely unlikely: identical public keys; fallback
                sendSalt = saltA;
                recvSalt = saltB;
            }

            Array.Clear(localPublic, 0, localPublic.Length);
            Array.Clear(remotePublic, 0, remotePublic.Length);

            var abyss = new AbyssStream(socket, ownsSocket: true, aeadKey: aeadKey, sendSalt: sendSalt, recvSalt: recvSalt);

            Array.Clear(aeadKey, 0, aeadKey.Length);
            Array.Clear(saltA, 0, saltA.Length);
            Array.Clear(saltB, 0, saltB.Length);

            return abyss;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            ThrowIfDisposed();

            // Serve leftover first if any (immediately return any available bytes)
            if (EnsureCurrentLeftoverSegment())
            {
                var seg = _currentLeftoverSegment;
                var avail = seg!.Length - _currentLeftoverOffset;
                var toCopy = Math.Min(avail, count);
                Array.Copy(seg, _currentLeftoverOffset, buffer, offset, toCopy);
                _currentLeftoverOffset += toCopy;
                if (_currentLeftoverOffset >= seg.Length)
                {
                    _currentLeftoverSegment = null;
                    _currentLeftoverOffset = 0;
                }
                return toCopy;
            }

            // No leftover -> read exactly one frame and decrypt
            var plaintext = await ReadOneFrameAndDecryptAsync(cancellationToken).ConfigureAwait(false);
            if (plaintext == null || plaintext.Length == 0)
            {
                // EOF
                return 0;
            }

            if (plaintext.Length <= count)
            {
                Array.Copy(plaintext, 0, buffer, offset, plaintext.Length);
                return plaintext.Length;
            }
            else
            {
                Array.Copy(plaintext, 0, buffer, offset, count);
                var leftoverLen = plaintext.Length - count;
                var leftover = new byte[leftoverLen];
                Array.Copy(plaintext, count, leftover, 0, leftoverLen);
                _leftoverQueue.Enqueue(leftover);
                return count;
            }
        }

        private async Task<byte[]?> ReadOneFrameAndDecryptAsync(CancellationToken cancellationToken)
        {
            var header = new byte[4];
            await ReadExactFromBaseAsync(header, 0, 4, cancellationToken).ConfigureAwait(false);

            var payloadLen = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
            if (payloadLen > MaxPlaintextFrame) throw new InvalidDataException("payload too big");
            if (payloadLen < AeadTagLen) throw new InvalidDataException("payload too small");

            var payload = new byte[payloadLen];
            await ReadExactFromBaseAsync(payload, 0, payloadLen, cancellationToken).ConfigureAwait(false);

            var ciphertextLen = payloadLen - AeadTagLen;
            var ciphertext = new byte[ciphertextLen];
            var tag = new byte[AeadTagLen];
            if (ciphertextLen > 0) Array.Copy(payload, 0, ciphertext, 0, ciphertextLen);
            Array.Copy(payload, ciphertextLen, tag, 0, AeadTagLen);

            // compute remote nonce using recv counter (no role bit)
            ulong remoteCounterValue = _recvCounter;
            _recvCounter++;

            var nonce = new byte[NonceLen];
            Array.Copy(_recvNonceSalt, 0, nonce, 0, NonceSaltLen);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(NonceSaltLen), remoteCounterValue);

            var plaintext = new byte[ciphertextLen];
            try
            {
                lock (_aeadLock)
                {
                    _aead.Decrypt(nonce, ciphertext, tag, plaintext);
                }
            }
            catch (CryptographicException)
            {
                Dispose();
                throw new CryptographicException("AEAD authentication failed; connection closed.");
            }
            finally
            {
                Array.Clear(nonce, 0, nonce.Length);
                Array.Clear(payload, 0, payload.Length);
                Array.Clear(ciphertext, 0, ciphertext.Length);
                Array.Clear(tag, 0, tag.Length);
            }

            return plaintext;
        }

        private async Task ReadExactFromBaseAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0) return;
            var read = 0;
            while (read < count)
            {
                var n = await base.ReadAsync(buffer, offset + read, count - read, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    if (read == 0)
                        throw new EndOfStreamException("Remote closed connection while reading.");
                    throw new EndOfStreamException("Remote closed connection unexpectedly during read.");
                }
                read += n;
            }
        }

        private static async Task ReadExactFromSocketAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0) return;
            var received = 0;
            while (received < count)
            {
                var mem = new Memory<byte>(buffer, offset + received, count - received);
                var r = await socket.ReceiveAsync(mem, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (r == 0)
                {
                    if (received == 0)
                        throw new EndOfStreamException("Remote closed connection while reading from socket.");
                    throw new EndOfStreamException("Remote closed connection unexpectedly during socket read.");
                }
                received += r;
            }
        }

        private static int CompareByteArrayLexicographic(byte[] a, byte[] b)
        {
            if (a == null || b == null) throw new ArgumentNullException();
            var min = Math.Min(a.Length, b.Length);
            for (int i = 0; i < min; i++)
            {
                if (a[i] < b[i]) return -1;
                if (a[i] > b[i]) return 1;
            }
            if (a.Length < b.Length) return -1;
            if (a.Length > b.Length) return 1;
            return 0;
        }

        private bool EnsureCurrentLeftoverSegment()
        {
            if (_currentLeftoverSegment != null && _currentLeftoverOffset < _currentLeftoverSegment.Length) return true;
            if (_leftoverQueue.TryDequeue(out var next))
            {
                _currentLeftoverSegment = next;
                _currentLeftoverOffset = 0;
                return true;
            }
            return false;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            ThrowIfDisposed();
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            ThrowIfDisposed();

            int remaining = count;
            int idx = offset;

            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, MaxPlaintextFrame);
                var mem = new ReadOnlyMemory<byte>(buffer, idx, chunk);
                await SendPlaintextChunkAsync(mem, cancellationToken).ConfigureAwait(false);
                idx += chunk;
                remaining -= chunk;
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task SendPlaintextChunkAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AeadTagLen];
            var nonce = new byte[NonceLen];
            ulong counterValue;

            lock (_sendLock)
            {
                counterValue = _sendCounter;
                _sendCounter++;
            }

            Array.Copy(_sendNonceSalt, 0, nonce, 0, NonceSaltLen);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(NonceSaltLen), counterValue);

            lock (_aeadLock)
            {
                _aead.Encrypt(nonce, plaintext.Span, ciphertext, tag);
            }

            var payloadLen = unchecked((uint)(ciphertext.Length + tag.Length));
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, payloadLen);

            await base.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            if (ciphertext.Length > 0)
                await base.WriteAsync(ciphertext, 0, ciphertext.Length, cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(tag, 0, tag.Length, cancellationToken).ConfigureAwait(false);
            await base.FlushAsync(cancellationToken).ConfigureAwait(false);

            Array.Clear(nonce, 0, nonce.Length);
            Array.Clear(tag, 0, tag.Length);
            Array.Clear(ciphertext, 0, ciphertext.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    lock (_aeadLock)
                    {
                        _aead.Dispose();
                    }

                    while (_leftoverQueue.TryDequeue(out var seg)) Array.Clear(seg, 0, seg.Length);
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        void IDisposable.Dispose() => Dispose();

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AbyssStream));
        }
        
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var tmp = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(tmp);
                Write(tmp, 0, buffer.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp, clearArray: true);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> seg))
            {
                return new ValueTask(WriteAsync(seg.Array!, seg.Offset, seg.Count, cancellationToken));
            }
            else
            {
                return SlowWriteAsync(buffer, cancellationToken);
            }

            async ValueTask SlowWriteAsync(ReadOnlyMemory<byte> buf, CancellationToken ct)
            {
                var tmp = ArrayPool<byte>.Shared.Rent(buf.Length);
                try
                {
                    buf.Span.CopyTo(tmp);
                    await WriteAsync(tmp, 0, buf.Length, ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tmp, clearArray: true);
                }
            }
        }
        
        public override int Read(Span<byte> buffer)
        {
            var tmp = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                int n = Read(tmp, 0, buffer.Length);
                new ReadOnlySpan<byte>(tmp, 0, n).CopyTo(buffer);
                return n;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp, clearArray: true);
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> seg))
            {
                return new ValueTask<int>(ReadAsync(seg.Array!, seg.Offset, seg.Count, cancellationToken));
            }
            else
            {
                return SlowReadAsync(buffer, cancellationToken);
            }

            async ValueTask<int> SlowReadAsync(Memory<byte> buf, CancellationToken ct)
            {
                var tmp = ArrayPool<byte>.Shared.Rent(buf.Length);
                try
                {
                    int n = await ReadAsync(tmp, 0, buf.Length, ct).ConfigureAwait(false);
                    new ReadOnlySpan<byte>(tmp, 0, n).CopyTo(buf.Span);
                    return n;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tmp, clearArray: true);
                }
            }
        }
    }

    public static class TcpClientAbyssExtensions
    {
        public static Task<AbyssStream> GetAbyssStreamAsync(this TcpClient client, UserService us, byte[]? privateKeyRaw = null, CancellationToken ct = default)
            => AbyssStream.CreateAsync(client, us, privateKeyRaw, ct);
    }
}
