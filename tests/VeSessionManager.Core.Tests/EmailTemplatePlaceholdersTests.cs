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
        "PaymentReminder5Day",
        "PaymentExpirationNotice",
        "FelonyDisclosureInstructions",
        "ArrlYouthProgramInstructions"
    ];

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
}
