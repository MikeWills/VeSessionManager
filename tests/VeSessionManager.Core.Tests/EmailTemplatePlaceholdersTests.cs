using VeSessionManager.Core.Email;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class EmailTemplatePlaceholdersTests
{
    // Must stay in sync with every key EmailDefaultsSeeder.SeedForTeamAsync seeds
    // (src/VeSessionManager.Worker/EmailDefaultsSeeder.cs) — if a new template Key is ever added
    // there without a matching registry entry, this test catches the drift.
    private static readonly string[] SeededKeys =
    [
        "RegistrationConfirmation",
        "DayBeforeReminder",
        "FccFeeReminder5Day",
        "PaymentExpirationNotice",
        "FelonyDisclosureInstructions",
        "ArrlYouthProgramInstructions",
        "GettingStartedLocally"
    ];

    /// <summary>
    /// The one template nobody's code sends: it is the starting text for a hand-composed message
    /// (#144), so what actually resolves is decided by <see cref="CandidatePlaceholderValues"/> rather
    /// than by a dictionary at a send site.
    ///
    /// <para>That makes this registry entry a promise the compose screen prints as insertable chips.
    /// Two lists, no compiler tying them together — which is the arrangement that has drifted here
    /// before. A token advertised but not resolved reaches a candidate as a literal
    /// <c>{{CallSign}}</c>; one resolved but not advertised is simply undiscoverable.</para>
    /// </summary>
    [Fact]
    public void GettingStartedLocally_AdvertisesExactlyWhatTheComposeScreenResolves()
    {
        Assert.Equal(CandidatePlaceholderValues.Names, EmailTemplatePlaceholders.For("GettingStartedLocally"));
    }

    [Theory]
    [MemberData(nameof(SeededKeyData))]
    public void ByKey_ContainsEntryForEverySeededKey(string key)
    {
        Assert.True(EmailTemplatePlaceholders.ByKey.ContainsKey(key));
        Assert.NotEmpty(EmailTemplatePlaceholders.ByKey[key]);
    }

    public static IEnumerable<object[]> SeededKeyData() => SeededKeys.Select(k => new object[] { k });

    [Fact]
    public void For_UnknownKey_ReturnsEmptyList_NotNull()
    {
        var result = EmailTemplatePlaceholders.For("SomeUnknownKey");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void For_KnownKey_ReturnsExpectedPlaceholders()
    {
        var result = EmailTemplatePlaceholders.For("ArrlYouthProgramInstructions");

        Assert.Equal(["CandidateName", "CallSign"], result);
    }

    [Fact]
    public void For_RegistrationConfirmation_IncludesYouthPaymentLinkUrl()
    {
        var result = EmailTemplatePlaceholders.For("RegistrationConfirmation");

        Assert.Contains("YouthPaymentLinkUrl", result);
    }
}
