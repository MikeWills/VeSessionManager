using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Square;

/// <summary>Per-Team Square merchant account credentials — TeamId keys SquareClient's internal per-team cached SDK client, since each team has its own separate Square account (not shared across teams). Environment travels with them: a token is issued for one API host and fails against the other.</summary>
public sealed record SquareCredentials(int TeamId, string AccessToken, string LocationId, SquareApiEnvironment Environment);

/// <summary>
/// Single definition of the Team -> SquareCredentials mapping, mirroring
/// <see cref="Email.TeamEmailCredentialsExtensions.ToEmailCredentials"/>.
///
/// <para>This was previously re-typed at five call sites — PaymentGenerationService (twice),
/// SquarePaymentLinkPurgeService, SquarePaymentMatchingService and YouthPaymentConfirmationService —
/// each repeating the same <c>SquareLocationId ?? ""</c> fallback. Adding the environment to a record
/// built in five places is exactly how one of them gets missed.</para>
/// </summary>
public static class TeamSquareCredentialsExtensions
{
    public static SquareCredentials ToSquareCredentials(this Team team) =>
        new(team.Id, team.SquareAccessToken!, team.SquareLocationId ?? "", team.SquareEnvironment);
}
