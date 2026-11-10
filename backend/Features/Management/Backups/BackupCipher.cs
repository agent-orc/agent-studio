using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Secrets;
using AgentStudio.Security;

namespace AgentStudio.Management;

/// <summary>
/// The backup encryption key: 32 random bytes, base64 in a file outside the
/// data directories and the backup directory. The owner escrows it next to
/// the secret store key; a backup cannot be read without it.
/// </summary>
public sealed class BackupKey
{
    public const int Size = 32;
    private readonly byte[] _material;

    public BackupKey(byte[] material)
    {
        if (material.Length != Size)
            throw new BackupException("backup-key-invalid", $"The backup key must be {Size} bytes; it holds {material.Length}.");
        _material = material.ToArray();
        Id = Fingerprint(_material);
    }

    /// <summary>Public 16-character fingerprint recorded in every archive header.</summary>
    public string Id { get; }

    public static string Fingerprint(byte[] material)
        => Convert.ToHexStringLower(SHA256.HashData([.. "agent-studio/backup-key-id/v1|"u8, .. material]))[..16];

    /// <summary>Per-archive key; the random salt makes every archive use its own AES key.</summary>
    internal byte[] DeriveArchiveKey(ReadOnlySpan<byte> salt)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, _material, 32, salt.ToArray(),
            Encoding.UTF8.GetBytes("agent-studio/backup/archive/v1"));

    public static BackupKey Load(string path)
    {
        if (!File.Exists(path)) throw new BackupException("backup-key-missing", "The backup key file does not exist.");
        OwnerOnlyAccess.RejectForeignAccess(path);
        var encoded = File.ReadAllText(path).Trim();
        CredentialRedactor.RegisterLiteral(encoded, pinned: true);
        try { return new BackupKey(Convert.FromBase64String(encoded)); }
        catch (FormatException) { throw new BackupException("backup-key-invalid", "The backup key file is not valid base64."); }
    }

    /// <summary>Creates a new owner-only key file; used by tests and the <c>--studio-backup key-create</c> command.</summary>
    public static void Create(string path)
    {
        OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var material = RandomNumberGenerator.GetBytes(Size);
        try
        {
            using var stream = OwnerOnlyAccess.CreateNewFile(path);
            stream.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(material)));
            stream.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(material); }
    }
}

/// <summary>
/// Archive encryption, format <c>STUDIOBK</c> version 1:
/// <code>
/// header  = "STUDIOBK" | 0x01 | key id (16 ASCII hex) | salt (32)
/// chunk   = length (4, big endian; top bit marks the final chunk) | ciphertext | tag (16)
/// </code>
/// Each chunk is AES-256-GCM under <c>HKDF(backup key, salt)</c> with the
/// nonce <c>counter (8) | 0 0 0 | final flag</c> and the header as associated
/// data. A reordered, dropped, truncated or appended chunk, a changed header
/// or a wrong key fails authentication. The plaintext never reaches the disk.
/// </summary>
public static class BackupCipher
{
    public const int ChunkSize = 1024 * 1024;
    internal static readonly byte[] Magic = "STUDIOBK"u8.ToArray();
    internal const byte Version = 1;
    internal const int HeaderSize = 8 + 1 + 16 + 32;
    private const uint FinalFlag = 0x8000_0000;
    private const int TagSize = 16;

    public static Stream Encrypt(Stream output, BackupKey key) => new EncryptingStream(output, key);

    public static Stream Decrypt(Stream input, BackupKey key) => new DecryptingStream(input, key);

    /// <summary>Reads the key id from an archive header without decrypting.</summary>
    public static string ReadKeyId(Stream input)
    {
        var header = new byte[HeaderSize];
        input.ReadExactly(header);
        ValidateHeader(header);
        return Encoding.ASCII.GetString(header, 9, 16);
    }

    private static void ValidateHeader(byte[] header)
    {
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new BackupException("backup-format-unknown", "The file is not a Studio backup.");
        if (header[8] != Version)
            throw new BackupException("backup-format-unknown", $"Backup format version {header[8]} is not supported.");
    }

    private static byte[] Nonce(ulong counter, bool final)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce, counter);
        nonce[11] = final ? (byte)1 : (byte)0;
        return nonce;
    }

    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _output;
        private readonly AesGcm _aes;
        private readonly byte[] _header;
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _buffered;
        private ulong _counter;
        private bool _finished;

        public EncryptingStream(Stream output, BackupKey key)
        {
            _output = output;
            var salt = RandomNumberGenerator.GetBytes(32);
            _header = [.. Magic, Version, .. Encoding.ASCII.GetBytes(key.Id), .. salt];
            var archiveKey = key.DeriveArchiveKey(salt);
            _aes = new AesGcm(archiveKey, TagSize);
            CryptographicOperations.ZeroMemory(archiveKey);
            _output.Write(_header);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_finished;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            if (_finished) throw new ObjectDisposedException(nameof(EncryptingStream));
            while (data.Length > 0)
            {
                // A full buffer is only sealed once more data arrives, so the
                // last chunk can always carry the final flag.
                if (_buffered == ChunkSize) Seal(final: false);
                var take = Math.Min(ChunkSize - _buffered, data.Length);
                data[..take].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += take;
                data = data[take..];
            }
        }

        private void Seal(bool final)
        {
            var cipher = new byte[_buffered];
            var tag = new byte[TagSize];
            _aes.Encrypt(Nonce(_counter++, final), _buffer.AsSpan(0, _buffered), cipher, tag, _header);
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)_buffered | (final ? FinalFlag : 0));
            _output.Write(length);
            _output.Write(cipher);
            _output.Write(tag);
            _buffered = 0;
        }

        public override void Flush() => _output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_finished)
            {
                _finished = true;
                Seal(final: true);
                _output.Flush();
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(_buffer);
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DecryptingStream : Stream
    {
        private readonly Stream _input;
        private readonly AesGcm _aes;
        private readonly byte[] _header = new byte[HeaderSize];
        private byte[] _plain = [];
        private int _offset;
        private ulong _counter;
        private bool _final;

        public DecryptingStream(Stream input, BackupKey key)
        {
            _input = input;
            try { _input.ReadExactly(_header); }
            catch (EndOfStreamException) { throw new BackupException("backup-truncated", "The backup ends inside its header."); }
            ValidateHeader(_header);
            var keyId = Encoding.ASCII.GetString(_header, 9, 16);
            if (!string.Equals(keyId, key.Id, StringComparison.Ordinal))
                throw new BackupException("backup-key-mismatch",
                    $"The backup was encrypted with key {keyId}; the supplied key is {key.Id}.");
            var archiveKey = key.DeriveArchiveKey(_header.AsSpan(25, 32));
            _aes = new AesGcm(archiveKey, TagSize);
            CryptographicOperations.ZeroMemory(archiveKey);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            while (_offset == _plain.Length)
            {
                if (_final) return 0;
                NextChunk();
            }
            var take = Math.Min(destination.Length, _plain.Length - _offset);
            _plain.AsSpan(_offset, take).CopyTo(destination);
            _offset += take;
            return take;
        }

        private void NextChunk()
        {
            var lengthBytes = new byte[4];
            if (!ReadFully(lengthBytes))
                throw new BackupException("backup-truncated", "The backup ends before its final chunk.");
            var raw = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
            var final = (raw & FinalFlag) != 0;
            var length = (int)(raw & ~FinalFlag);
            if (length > ChunkSize) throw new BackupException("backup-corrupt", "A backup chunk is larger than the format allows.");
            var cipher = new byte[length];
            var tag = new byte[TagSize];
            if (!ReadFully(cipher) || !ReadFully(tag))
                throw new BackupException("backup-truncated", "The backup ends inside a chunk.");
            var plain = new byte[length];
            try { _aes.Decrypt(Nonce(_counter++, final), cipher, tag, plain, _header); }
            catch (AuthenticationTagMismatchException)
            {
                throw new BackupException("backup-corrupt", "The backup failed authentication: it was changed or the key is wrong.");
            }
            if (final && _input.ReadByte() != -1)
                throw new BackupException("backup-corrupt", "The backup has data after its final chunk.");
            _final = final;
            _plain = plain;
            _offset = 0;
        }

        private bool ReadFully(byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = _input.Read(buffer, read, buffer.Length - read);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _aes.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>A backup failure with a stable code for the API and the status record.</summary>
public sealed class BackupException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
