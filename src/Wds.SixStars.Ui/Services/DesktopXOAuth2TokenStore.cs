using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wds.SixStars.Api.XPosting;

namespace Wds.SixStars.Ui.Services;

/// <summary>Stores OAuth 2.0 tokens encrypted for the current Windows user with DPAPI.</summary>
public sealed class DesktopXOAuth2TokenStore : IXOAuth2TokenStore
{
    private readonly string _rootDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    public DesktopXOAuth2TokenStore(string rootDirectory)
    {
        _rootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine("state", "x-posting", "oauth2-tokens")
            : rootDirectory);
    }

    public async Task<StarXOAuth2Token?> LoadAsync(string accountId, CancellationToken cancellationToken)
    {
        string normalizedId = NormalizeAccountId(accountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(normalizedId);
            if (!File.Exists(path)) return null;
            byte[] encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] plain = ProtectedData.Unprotect(encrypted, GetEntropy(normalizedId), DataProtectionScope.CurrentUser);
            StarXOAuth2Token? token = JsonSerializer.Deserialize<StarXOAuth2Token>(plain, _jsonOptions);
            if (token == null || !string.Equals(NormalizeAccountId(token.AccountId), normalizedId, StringComparison.OrdinalIgnoreCase)) return null;
            return token;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(StarXOAuth2Token token, CancellationToken cancellationToken)
    {
        if (token == null) throw new ArgumentNullException(nameof(token));
        string normalizedId = NormalizeAccountId(token.AccountId);
        if (string.IsNullOrWhiteSpace(normalizedId)) throw new ArgumentException("OAuth token AccountId is required.", nameof(token));
        token.AccountId = normalizedId;
        token.UpdatedAt = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(token, _jsonOptions);
            byte[] encrypted = ProtectedData.Protect(plain, GetEntropy(normalizedId), DataProtectionScope.CurrentUser);
            string path = GetPath(normalizedId);
            string temporaryPath = path + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string accountId, CancellationToken cancellationToken)
    {
        string normalizedId = NormalizeAccountId(accountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(normalizedId);
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string accountId) => Path.Combine(_rootDirectory, accountId + ".oauth2.dat");

    private static byte[] GetEntropy(string accountId)
    {
        return Encoding.UTF8.GetBytes("Wds.SixStars.XOAuth2." + accountId);
    }

    private static string NormalizeAccountId(string? accountId)
    {
        string value = (accountId ?? string.Empty).Trim().ToLowerInvariant();
        StringBuilder safe = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') safe.Append(c);
        }
        return safe.ToString();
    }
}
