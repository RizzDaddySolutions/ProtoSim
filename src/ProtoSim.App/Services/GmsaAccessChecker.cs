using System.DirectoryServices;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace ProtoSim.App.Services;

/// <summary>Outcome of a gMSA access check against a REST endpoint.</summary>
/// <param name="HasAccess">True when the endpoint returned a 2xx status as the gMSA.</param>
/// <param name="StatusCode">HTTP status returned, or null if the request never got a response.</param>
/// <param name="Message">Human-readable detail (reason, error text).</param>
public sealed record GmsaAccessResult(bool HasAccess, HttpStatusCode? StatusCode, string Message);

/// <summary>
/// Tests whether a group Managed Service Account (gMSA) can call a Windows-authenticated
/// (Negotiate/Kerberos/NTLM) REST API while the app runs as a normal user.
/// </summary>
/// <remarks>
/// The current user retrieves the gMSA's managed password from AD (msDS-ManagedPassword) and
/// authenticates to the endpoint with it. This only works if the current user (or a group they're in)
/// is listed in the gMSA's PrincipalsAllowedToRetrieveManagedPassword. Intended for testing only.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GmsaAccessChecker
{
    private readonly string? _domainController;

    /// <param name="domainController">Optional DC/domain to query (e.g. "corp.example.com"). Null uses the current domain.</param>
    public GmsaAccessChecker(string? domainController = null) => _domainController = domainController;

    /// <summary>Calls <paramref name="endpoint"/> as <paramref name="gmsaAccount"/> and reports whether access was granted.</summary>
    /// <param name="gmsaAccount">gMSA name: "svc-api", "svc-api$" or "DOMAIN\svc-api$".</param>
    /// <param name="endpoint">Absolute URL of the REST endpoint.</param>
    /// <param name="method">HTTP method to use; defaults to GET.</param>
    public async Task<GmsaAccessResult> CheckAccessAsync(
        string gmsaAccount, string endpoint, HttpMethod? method = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gmsaAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("Endpoint must be an absolute URL.", nameof(endpoint));

        var (domain, samAccountName) = ParseAccount(gmsaAccount);

        NetworkCredential credential;
        try
        {
            credential = GetGmsaCredential(samAccountName, domain);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new GmsaAccessResult(false, null,
                $"Could not retrieve the gMSA password for '{samAccountName}': {ex.Message} " +
                "Make sure your account is in the gMSA's PrincipalsAllowedToRetrieveManagedPassword.");
        }

        // Restrict the credential to Windows auth schemes so the password is never sent via Basic/Digest.
        var credentials = new CredentialCache
        {
            { uri, "Negotiate", credential },
            { uri, "NTLM", credential },
        };

        using var handler = new SocketsHttpHandler
        {
            Credentials = credentials,
            PreAuthenticate = false,
            UseCookies = false,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var code = response.StatusCode;
            return code switch
            {
                _ when response.IsSuccessStatusCode =>
                    new(true, code, $"Access granted ({(int)code} {code})."),
                HttpStatusCode.Unauthorized =>
                    new(false, code, "401 Unauthorized: authentication failed or the endpoint doesn't accept Windows auth."),
                HttpStatusCode.Forbidden =>
                    new(false, code, "403 Forbidden: authenticated as the gMSA but not authorized."),
                _ =>
                    new(false, code, $"Unexpected response {(int)code} {code}; access could not be confirmed."),
            };
        }
        catch (HttpRequestException ex)
        {
            return new GmsaAccessResult(false, null, $"Request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GmsaAccessResult(false, null, "Request timed out.");
        }
    }

    private static (string? Domain, string SamAccountName) ParseAccount(string account)
    {
        string? domain = null;
        var name = account.Trim();

        var slash = name.IndexOf('\\');
        if (slash >= 0)
        {
            domain = name[..slash];
            name = name[(slash + 1)..];
        }
        else if (name.IndexOf('@') is var at and >= 0)
        {
            domain = name[(at + 1)..];
            name = name[..at];
        }

        if (!name.EndsWith('$'))
            name += "$";

        return (domain, name);
    }

    private NetworkCredential GetGmsaCredential(string samAccountName, string? domain)
    {
        var path = string.IsNullOrEmpty(_domainController) ? "LDAP://RootDSE" : $"LDAP://{_domainController}/RootDSE";
        // msDS-ManagedPassword is only returned over a signed + sealed (encrypted) connection.
        const AuthenticationTypes auth = AuthenticationTypes.Secure | AuthenticationTypes.Signing | AuthenticationTypes.Sealing;

        using var rootDse = new DirectoryEntry(path, null, null, auth);
        var namingContext = (string)rootDse.Properties["defaultNamingContext"].Value!;
        var searchRoot = string.IsNullOrEmpty(_domainController)
            ? $"LDAP://{namingContext}"
            : $"LDAP://{_domainController}/{namingContext}";

        using var root = new DirectoryEntry(searchRoot, null, null, auth);
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=msDS-GroupManagedServiceAccount)(sAMAccountName={EscapeLdap(samAccountName)}))",
            SearchScope = SearchScope.Subtree,
        };
        searcher.PropertiesToLoad.Add("msDS-ManagedPassword");

        var result = searcher.FindOne()
            ?? throw new InvalidOperationException($"gMSA '{samAccountName}' was not found in {namingContext}.");

        if (result.Properties["msDS-ManagedPassword"] is not { Count: > 0 } values || values[0] is not byte[] blob)
            throw new UnauthorizedAccessException("msDS-ManagedPassword was not returned (access denied).");

        var password = ParseCurrentPassword(blob);
        domain ??= NetBiosOrDnsFromNamingContext(namingContext);
        return new NetworkCredential(samAccountName, password, domain);
    }

    /// <summary>Extracts CurrentPassword from an MSDS-MANAGEDPASSWORD_BLOB.</summary>
    private static string ParseCurrentPassword(byte[] blob)
    {
        // Layout: Version(2) Reserved(2) Length(4) CurrentPasswordOffset(2) PreviousPasswordOffset(2) ...
        if (blob.Length < 16)
            throw new InvalidOperationException("Managed password blob is too short.");

        int offset = BitConverter.ToUInt16(blob, 8);
        var chars = MemoryMarshal.Cast<byte, char>(blob.AsSpan(offset, (blob.Length - offset) & ~1));
        var end = chars.IndexOf('\0');
        if (end < 0)
            throw new InvalidOperationException("Managed password blob is malformed.");

        // Build the string from raw UTF-16 code units. The password is random binary and can contain
        // unpaired surrogates, which Encoding.Unicode.GetString would replace with U+FFFD and corrupt.
        return new string(chars[..end]);
    }

    private static string NetBiosOrDnsFromNamingContext(string namingContext) =>
        string.Join('.', namingContext.Split(',')
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..]));

    private static string EscapeLdap(string value) => value
        .Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
}
