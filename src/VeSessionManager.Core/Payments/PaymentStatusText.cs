namespace VeSessionManager.Core.Payments;

/// <summary>
/// The one definition of a payment status's plain-English label — shared between the session
/// roster's chip (VeSessionManager.Web.SessionChips.Payment, which pairs this with a CSS class) and
/// the <c>{{PaymentStatus}}</c> message-rule placeholder (BeforeSessionStartScanner), which Core
/// can use directly since it carries no CSS/Web dependency. One wording, not two that could drift.
/// </summary>
public static class PaymentStatusText
{
    /// <param name="status">
    /// Null means no <see cref="Entities.Payment"/> row exists at all — distinct from
    /// <see cref="Entities.PaymentStatus.NotApplicable"/>, which is a payment that exists and is not
    /// owed. Collapsing the two would report "no payment" for a session that collects no fees.
    /// </param>
    public static string For(Entities.PaymentStatus? status) => status switch
    {
        null => "No payment",
        Entities.PaymentStatus.Paid => "Paid",
        Entities.PaymentStatus.Unpaid => "Unpaid",
        _ => "Not applicable"
    };
}
