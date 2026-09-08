using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Retention;

public sealed record ArchiveObjectReference(
    string Target,
    string ObjectKey,
    string? ETag,
    string Sha256,
    long Size,
    string? ServerChecksumSha256 = null);

public sealed record ArchiveObjectVerification(bool Exists, bool Verified, string? ActualSha256, string? Error);

public interface IArchiveTarget
{
    string Name { get; }
    Task<ArchiveObjectReference> PutAsync(
        string objectKey,
        Stream content,
        string sha256,
        long size,
        CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);
    Task<ArchiveObjectVerification> VerifyAsync(
        ArchiveObjectReference reference,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}

public sealed class LocalDirectoryTarget(string rootPath) : IArchiveTarget
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);
    public string Name => "local";

    public async Task<ArchiveObjectReference> PutAsync(
        string objectKey, Stream content, string sha256, long size, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".writing-{Guid.NewGuid():N}";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(output, cancellationToken);
            if (new FileInfo(temporary).Length != size)
                throw new InvalidDataException($"Archive object size mismatch while writing '{objectKey}'.");
            var actual = await HashFileAsync(temporary, cancellationToken);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive object hash mismatch while writing '{objectKey}'.");
            File.Move(temporary, path, overwrite: false);
            return new ArchiveObjectReference(Name, objectKey, null, sha256, size, sha256);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = File.OpenRead(Resolve(objectKey));
        return Task.FromResult(stream);
    }

    public async Task<ArchiveObjectVerification> VerifyAsync(
        ArchiveObjectReference reference, CancellationToken cancellationToken = default)
    {
        var path = Resolve(reference.ObjectKey);
        if (!File.Exists(path)) return new(false, false, null, "object-missing");
        var actual = await HashFileAsync(path, cancellationToken);
        return new(true, string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase), actual,
            string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase) ? null : "sha256-mismatch");
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(objectKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public string Resolve(string objectKey)
    {
        var normalized = objectKey.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var root = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new ArgumentException("Archive object key resolves outside the local target.", nameof(objectKey));
        return path;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }
}

public sealed record S3TargetOptions
{
    public required Uri Endpoint { get; init; }
    public required string Bucket { get; init; }
    public string Prefix { get; init; } = "";
    public string Region { get; init; } = "us-east-1";
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public bool PathStyle { get; init; }
    public bool ServerSideChecksum { get; init; } = true;

    public static S3TargetOptions FromSecretFile(
        Uri endpoint,
        string bucket,
        string prefix,
        string secretFile,
        bool pathStyle,
        bool serverSideChecksum,
        string region = "us-east-1")
    {
        if (!File.Exists(secretFile))
            throw new FileNotFoundException("The S3 archive credential file does not exist.", secretFile);
        var credential = JsonSerializer.Deserialize<S3CredentialFile>(File.ReadAllText(secretFile),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The S3 archive credential file is invalid.");
        if (string.IsNullOrWhiteSpace(credential.AccessKey) || string.IsNullOrWhiteSpace(credential.SecretKey))
            throw new InvalidDataException("The S3 archive credential file requires accessKey and secretKey.");
        return new S3TargetOptions
        {
            Endpoint = endpoint,
            Bucket = RequireSegment(bucket, nameof(bucket)),
            Prefix = prefix.Trim('/'),
            Region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region.Trim(),
            AccessKey = credential.AccessKey,
            SecretKey = credential.SecretKey,
            PathStyle = pathStyle,
            ServerSideChecksum = serverSideChecksum,
        };
    }

    private static string RequireSegment(string value, string parameter)
        => string.IsNullOrWhiteSpace(value) || value.Contains('/')
            ? throw new ArgumentException("S3 bucket must be a non-empty bucket name.", parameter)
            : value;

    private sealed record S3CredentialFile(string AccessKey, string SecretKey);
}

/// <summary>
/// Small S3 SigV4 client used by retention so every S3-compatible endpoint works without a provider-specific SDK.
/// It signs PUT, GET, HEAD, and DELETE requests and verifies object bytes independently of ETags.
/// </summary>
public sealed class S3Target : IArchiveTarget, IDisposable
{
    private readonly S3TargetOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public S3Target(S3TargetOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _ownsHttp = httpClient is null;
    }

    public string Name => "s3";

    public async Task<ArchiveObjectReference> PutAsync(
        string objectKey, Stream content, string sha256, long size, CancellationToken cancellationToken = default)
    {
        objectKey = NormalizeStoredKey(objectKey);
        using var request = CreateRequest(HttpMethod.Put, objectKey, sha256);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentLength = size;
        request.Headers.TryAddWithoutValidation("x-amz-meta-sha256", sha256);
        if (_options.ServerSideChecksum)
            request.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", Convert.ToBase64String(Convert.FromHexString(sha256)));
        Sign(request, sha256, DateTimeOffset.UtcNow);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "upload", objectKey, cancellationToken);
        var reference = new ArchiveObjectReference(
            Name,
            objectKey,
            response.Headers.ETag?.Tag?.Trim('"'),
            sha256,
            size,
            Header(response, "x-amz-checksum-sha256"));
        var verification = await VerifyAsync(reference, cancellationToken);
        if (!verification.Verified)
            throw new InvalidDataException($"S3 object verification failed for '{objectKey}': {verification.Error}");
        return reference;
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        objectKey = NormalizeStoredKey(objectKey);
        using var request = CreateRequest(HttpMethod.Get, objectKey, EmptySha256);
        Sign(request, EmptySha256, DateTimeOffset.UtcNow);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new FileNotFoundException($"S3 archive object is missing: {objectKey}");
        }
        await EnsureSuccessAsync(response, "download", objectKey, cancellationToken);
        return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken), response);
    }

    public async Task<ArchiveObjectVerification> VerifyAsync(
        ArchiveObjectReference reference, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var content = await OpenReadAsync(reference.ObjectKey, cancellationToken);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
            var verified = string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase);
            return new(true, verified, actual, verified ? null : "sha256-mismatch");
        }
        catch (FileNotFoundException)
        {
            return new(false, false, null, "object-missing");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new(false, false, null, exception.Message);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        objectKey = NormalizeStoredKey(objectKey);
        using var request = CreateRequest(HttpMethod.Delete, objectKey, EmptySha256);
        Sign(request, EmptySha256, DateTimeOffset.UtcNow);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "delete", objectKey, cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private string WithPrefix(string key)
    {
        key = key.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(_options.Prefix) ? key : $"{_options.Prefix}/{key}";
    }

    private string NormalizeStoredKey(string key)
    {
        key = key.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(_options.Prefix) || key.StartsWith(_options.Prefix + "/", StringComparison.Ordinal))
            return key;
        return WithPrefix(key);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string key, string payloadSha256)
    {
        var endpoint = _options.Endpoint;
        var escapedKey = string.Join('/', key.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        Uri uri;
        if (_options.PathStyle)
        {
            var path = $"{endpoint.AbsolutePath.TrimEnd('/')}/{Uri.EscapeDataString(_options.Bucket)}/{escapedKey}";
            uri = new UriBuilder(endpoint) { Path = path }.Uri;
        }
        else
        {
            var builder = new UriBuilder(endpoint)
            {
                Host = $"{_options.Bucket}.{endpoint.Host}",
                Path = $"{endpoint.AbsolutePath.TrimEnd('/')}/{escapedKey}",
            };
            uri = builder.Uri;
        }
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadSha256);
        return request;
    }

    private void Sign(HttpRequestMessage request, string payloadSha256, DateTimeOffset now)
    {
        var timestamp = now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);

        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = request.RequestUri!.IsDefaultPort
                ? request.RequestUri.Host
                : $"{request.RequestUri.Host}:{request.RequestUri.Port}",
            ["x-amz-content-sha256"] = payloadSha256,
            ["x-amz-date"] = timestamp,
        };
        foreach (var name in new[] { "x-amz-checksum-sha256", "x-amz-meta-sha256" })
            if (request.Headers.TryGetValues(name, out var values)) signed[name] = string.Join(',', values).Trim();
        var signedNames = string.Join(';', signed.Keys);
        var canonicalHeaders = string.Concat(signed.Select(pair => $"{pair.Key}:{pair.Value}\n"));
        var canonicalRequest = string.Join("\n",
            request.Method.Method,
            request.RequestUri.AbsolutePath,
            request.RequestUri.Query.TrimStart('?'),
            canonicalHeaders,
            signedNames,
            payloadSha256);
        var scope = $"{date}/{_options.Region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{HexSha256(canonicalRequest)}";
        var signature = Convert.ToHexStringLower(Hmac(SigningKey(date), stringToSign));
        request.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            $"Credential={_options.AccessKey}/{scope}, SignedHeaders={signedNames}, Signature={signature}");
    }

    private byte[] SigningKey(string date)
    {
        var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + _options.SecretKey), date);
        var regionKey = Hmac(dateKey, _options.Region);
        var serviceKey = Hmac(regionKey, "s3");
        return Hmac(serviceKey, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string HexSha256(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, string key, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 512) detail = detail[..512];
        throw new HttpRequestException($"S3 {operation} failed for '{key}' with {(int)response.StatusCode}: {detail}");
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
