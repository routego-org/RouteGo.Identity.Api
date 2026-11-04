using RouteGo.Identity.Api.Common.Security;

namespace RouteGo.Identity.Api.Features.Authentication.Logout;

/// <summary>The refresh token of the session to end.</summary>
/// <param name="RefreshToken">The refresh token this device holds.</param>
public sealed record LogoutRequest(string? RefreshToken);

public sealed class LogoutValidator : AbstractValidator<LogoutRequest>
{
    public LogoutValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public sealed class LogoutHandler
{
    private const string AuditAction = "Logout";

    // Ownership is in the WHERE clause: a token that is not the caller's revokes nothing.
    private const string RevokeOwnChainSql = """
        UPDATE authentication.refresh_tokens
        SET revoked_at = now(), revoke_reason = 'SignedOut'
        WHERE revoked_at IS NULL
          AND chain_id = (SELECT chain_id FROM authentication.refresh_tokens
                          WHERE token_hash = @TokenHash AND user_id = @UserId)
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public LogoutHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>Revokes the presented token's chain when it is the caller's; otherwise changes nothing.</summary>
    /// <param name="callerUserId">The signed-in account, from the access token.</param>
    /// <param name="request">The refresh token to end.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task HandleAsync(Guid callerUserId, LogoutRequest request, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var parameters = new { TokenHash = OpaqueToken.Hash(request.RefreshToken!), UserId = callerUserId };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            RevokeOwnChainSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/sessions/revoke")]
public sealed class LogoutController : ControllerBase
{
    private readonly LogoutHandler _handler;
    private readonly LogoutValidator _validator = new LogoutValidator();

    public LogoutController(IWriterConnectionFactory writerFactory)
    {
        _handler = new LogoutHandler(writerFactory);
    }

    /// <summary>Sign out this device.</summary>
    /// <response code="204">Signed out of this device.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    [Authorize]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var callerUserId = Guid.Parse(User.FindFirst("sub")!.Value);
        await _handler.HandleAsync(callerUserId, request, cancellationToken);
        return NoContent();
    }
}
