using System.Globalization;
using VeSessionManager.Core;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Pins the behaviour <see cref="Usd"/> exists for: money formats and parses in the invariant
/// culture, never the ambient one.
///
/// <para>Every assertion here runs under <c>de-DE</c>, which uses <c>,</c> as its decimal separator.
/// That is the culture that made the original bugs visible — a bare <c>$"{amount:F2}"</c> renders
/// <c>12,50</c> there, and <c>decimal.TryParse("12.50")</c> reads it as <b>1250</b> (issues #271 and
/// #308). Under the default test culture both spellings pass, which is exactly why this needs to
/// state the culture explicitly rather than trusting the runner's.</para>
/// </summary>
public class UsdTests
{
    /// <summary>A comma-decimal culture, so an ambient-culture bug cannot pass unnoticed.</summary>
    private static void InCommaDecimalCulture(Action assertions)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            assertions();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FormatUsesAPeriodAndADollarSignRegardlessOfCulture() =>
        InCommaDecimalCulture(() =>
        {
            Assert.Equal("$12.50", Usd.Format(12.5m));
            Assert.Equal("$0.00", Usd.Format(0m));
            Assert.Equal("$1500.00", Usd.Format(1500m));
        });

    [Fact]
    public void FormatNeverProducesTheInvariantCurrencySign() =>
        // The trap "C" formatting falls into: the invariant culture's currency symbol is the
        // generic sign, not a dollar. Asserted rather than described so nobody "simplifies" to "C".
        Assert.DoesNotContain('¤', Usd.Format(12.5m));

    [Fact]
    public void NullFormatsAsAnEmDashRatherThanZero() =>
        // $0.00 would read as a deliberate decision to charge nothing; the fee tables mean "not set".
        Assert.Equal("—", Usd.Format((decimal?)null));

    [Fact]
    public void RawOmitsTheSymbolSoItCanGoInANumberInput() =>
        InCommaDecimalCulture(() => Assert.Equal("12.50", Usd.Raw(12.5m)));

    [Fact]
    public void TryParseReadsAPeriodAsTheDecimalSeparatorRegardlessOfCulture() =>
        InCommaDecimalCulture(() =>
        {
            Assert.True(Usd.TryParse("12.50", out var amount));
            Assert.Equal(12.5m, amount);
        });

    /// <summary>
    /// The original bug, stated as a test: under <c>de-DE</c> the framework's own parse reads
    /// <c>"12.50"</c> as twelve hundred and fifty, because <c>.</c> is its thousands separator. A
    /// retained-amount override of $12.50 became $1,250.00.
    /// </summary>
    [Fact]
    public void TryParseDoesNotReadAPeriodAsAThousandsSeparator() =>
        InCommaDecimalCulture(() =>
        {
            Assert.True(decimal.TryParse("12.50", out var ambient));
            Assert.Equal(1250m, ambient); // what the old code did

            Assert.True(Usd.TryParse("12.50", out var invariant));
            Assert.Equal(12.5m, invariant); // what it does now
        });

    [Fact]
    public void TryParseRejectsACurrencySymbol()
    {
        // NumberStyles.Number deliberately excludes AllowCurrencySymbol: a posted "$12.50" is a
        // malformed number, and the caller should say so rather than silently accept it.
        Assert.False(Usd.TryParse("$12.50", out _));
        Assert.False(Usd.TryParse("abc", out _));
        Assert.False(Usd.TryParse(null, out _));
    }

    [Fact]
    public void FormatAndTryParseRoundTrip() =>
        InCommaDecimalCulture(() =>
        {
            foreach (var amount in new[] { 0m, 0.01m, 12.5m, 15m, 1234.56m })
            {
                Assert.True(Usd.TryParse(Usd.Raw(amount), out var parsed));
                Assert.Equal(amount, parsed);
            }
        });
}
