using System.Globalization;

namespace VeSessionManager.Core;

/// <summary>
/// The one place money becomes a string, and the one place a string becomes money.
///
/// <para><b>Never use <c>"C"</c>.</b> The invariant culture's currency symbol is the generic
/// <c>¤</c>, not <c>$</c> — so <c>decimal.ToString("C", CultureInfo.InvariantCulture)</c> renders
/// <c>¤12.50</c>. That was caught in Phase 6 before it shipped, and the workaround (a literal
/// <c>$</c> plus <c>"F2"</c>) was then re-typed at roughly sixteen call sites in two spellings
/// (issue #308). This type is that workaround, written once.</para>
///
/// <para><b>The culture argument is the part that matters, and most call sites were missing it.</b>
/// A bare <c>$"${amount:F2}"</c> formats in the <i>ambient</i> culture, so under a comma-decimal
/// request culture it produces <c>$12,50</c> — and the parse half had the mirror-image bug, where
/// <c>decimal.TryParse("12,50")</c> yields <b>1250</b> (issue #271). Formatting and parsing money
/// through the same invariant culture is what makes a value survive the round trip through a form.
/// Only <c>PaymentReminderService</c> was already doing this, with the comment that became the
/// paragraph above.</para>
///
/// <para>This app is US-only (FCC/ARRL), so a literal <c>$</c> is simpler and more correct than
/// culture-driven currency formatting. If that ever stops being true, this is the one file to
/// change.</para>
/// </summary>
public static class Usd
{
    /// <summary>For display: <c>$12.50</c>.</summary>
    public static string Format(decimal amount) => "$" + Raw(amount);

    /// <summary>
    /// For display where the value is optional. Defaults to an em dash, which is what the fee and
    /// payment tables already render for "not set" — a <c>$0.00</c> there would read as a real
    /// decision to charge nothing.
    /// </summary>
    public static string Format(decimal? amount, string whenNull = "—") =>
        amount is null ? whenNull : Format(amount.Value);

    /// <summary>
    /// The digits alone, no symbol — for an <c>&lt;input type="number"&gt;</c> value, which must use
    /// <c>.</c> as the decimal separator regardless of the user's locale or the browser refuses it.
    /// Do not use this for anything a human reads as a price.
    /// </summary>
    public static string Raw(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The inverse of <see cref="Raw"/>. Invariant so it round-trips with what the form rendered;
    /// <see cref="NumberStyles.Number"/> so thousands separators and a leading sign are tolerated
    /// but a currency symbol is not — a posted <c>"$12.50"</c> is a malformed number, not a price,
    /// and the caller should say so rather than silently accept it.
    /// </summary>
    public static bool TryParse(string? text, out decimal amount) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
}
