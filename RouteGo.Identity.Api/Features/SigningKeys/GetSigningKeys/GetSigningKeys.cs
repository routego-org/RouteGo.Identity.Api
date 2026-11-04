using System.Buffers.Text;
using System.Security.Cryptography;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.SigningKeys.GetSigningKeys;

/// <summary>One RSA public key in JSON Web Key form; the names are the standard's, read verbatim by token libraries.</summary>
public sealed class PublicKey
{
    /// <summary>Key type — always "RSA".</summary>
    public required string Kty { get; init; }

    /// <summary>Intended use — always "sig".</summary>
    public required string Use { get; init; }

    /// <summary>Signing algorithm — always "RS256".</summary>
    public required string Alg { get; init; }

    /// <summary>The key ID that access tokens signed with this key carry.</summary>
    public required string Kid { get; init; }

    /// <summary>RSA modulus, base64url-encoded.</summary>
    public required string N { get; init; }

    /// <summary>RSA public exponent, base64url-encoded.</summary>
    public required string E { get; init; }
}

/// <summary>The public key set: every introduced, active and previous key; never a retired one.</summary>
/// <param name="Keys">The published keys.</param>
public sealed record GetSigningKeysResponse(IReadOnlyList<PublicKey> Keys);

public sealed class GetSigningKeysHandler
{
    private const string SelectPublishedSql = """
        SELECT key_id, public_key
        FROM signing_keys.signing_keys
        WHERE state = ANY(@States)
        ORDER BY introduced_at
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public GetSigningKeysHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    public async Task<GetSigningKeysResponse> HandleAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = _readerFactory.CreateReadConnection();
        IEnumerable<KeyRow> rows = await connection.QueryAsync<KeyRow>(new CommandDefinition(
            SelectPublishedSql, new { States = SigningKeyStates.Published.ToArray() }, cancellationToken: cancellationToken));

        var keys = new List<PublicKey>();
        foreach (KeyRow row in rows)
        {
            keys.Add(ToJsonWebKey(row));
        }

        return new GetSigningKeysResponse(keys);
    }

    // Only the public parameters are exported, so no private component can reach the response even
    // if a private key were ever stored by mistake.
    private static PublicKey ToJsonWebKey(KeyRow row)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(row.PublicKey);
        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
        return new PublicKey
        {
            Kty = "RSA",
            Use = "sig",
            Alg = "RS256",
            Kid = row.KeyId,
            N = Base64Url.EncodeToString(parameters.Modulus),
            E = Base64Url.EncodeToString(parameters.Exponent),
        };
    }

    private sealed record KeyRow(string KeyId, string PublicKey);
}

[ApiController]
[ApiExplorerSettings(GroupName = "SigningKeys")]
[Route(".well-known/jwks.json")]
public sealed class GetSigningKeysController : ControllerBase
{
    // Binds every cache on the way, the CDN and proxy in front of Identity included, so none can
    // serve a set older than the 10-minute wait before a newly introduced key starts signing.
    // Written verbatim rather than through [ResponseCache], which drops the space the contract fixes.
    private const string CacheControl = "public, max-age=300";

    private readonly GetSigningKeysHandler _handler;

    public GetSigningKeysController(IReaderConnectionFactory readerFactory)
    {
        _handler = new GetSigningKeysHandler(readerFactory);
    }

    /// <summary>Get the public key set every RouteGo service validates access tokens against.</summary>
    /// <response code="200">The current public key set.</response>
    [AllowAnonymous] // Public by design: services fetch it before they can hold a token, and it holds nothing secret.
    [HttpGet]
    [ProducesResponseType(typeof(GetSigningKeysResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSigningKeys(CancellationToken cancellationToken)
    {
        GetSigningKeysResponse response = await _handler.HandleAsync(cancellationToken);
        Response.Headers.CacheControl = CacheControl;
        return Ok(response);
    }
}
