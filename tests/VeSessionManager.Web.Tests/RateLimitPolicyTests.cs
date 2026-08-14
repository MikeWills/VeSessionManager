using Microsoft.AspNetCore.Http;
using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Which rate-limit bucket each path falls into (#264).
///
/// <para>The rule is prefix matching with a default of <c>Unlimited</c>, which means <b>a new
/// endpoint gets no limit and nothing says so</b>. That is how the Square webhook — public by
/// necessity, and costing a <c>Teams.FindAsync</c> plus an HMAC-SHA256 over up to 64 KB before it
/// can reject anything — ended up outside every partition.</para>
///
/// <para>Asserted here rather than by firing hundreds of requests at the test host: the harness sets
/// <c>GlobalLimiter = null</c> so a crawl of /Account does not trip the bucket and produce 429s that
/// look like page failures. Testing the decision directly is both faster and the thing that was
/// actually wrong.</para>
/// </summary>
public class RateLimitPolicyTests
{
    [Theory]
    [InlineData("/webhooks/square/1")]
    [InlineData("/webhooks/square/42")]
    [InlineData("/webhooks")]
    public void WebhookPathsGetTheWebhookBucket(string path) =>
        Assert.Equal(RateLimitPolicy.Bucket.Webhook, RateLimitPolicy.For(new PathString(path)));

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/ForgotPassword")]
    [InlineData("/VeSelfService/SignIn")]
    public void AbusableHumanPathsGetTheInteractiveBucket(string path) =>
        Assert.Equal(RateLimitPolicy.Bucket.Interactive, RateLimitPolicy.For(new PathString(path)));

    [Theory]
    [InlineData("/SessionManager/Index")]
    [InlineData("/Admin/Users")]
    [InlineData("/css/app.css")]
    [InlineData("/")]
    public void EverythingElseIsUnlimited(string path) =>
        Assert.Equal(RateLimitPolicy.Bucket.Unlimited, RateLimitPolicy.For(new PathString(path)));

    /// <summary>
    /// The webhook allowance has to be well above Interactive's. Square bursts legitimately —
    /// retries, a batch of payments settling together — and a delivery dropped as 429 is a payment
    /// this app never records, with the first symptom being a candidate insisting they paid.
    /// </summary>
    [Fact]
    public void TheWebhookBucketIsFarMoreGenerousThanTheInteractiveOne() =>
        Assert.True(RateLimitPolicy.WebhookPermitLimit >= RateLimitPolicy.InteractivePermitLimit * 10,
            "A webhook limit close to the interactive one will drop Square deliveries during an ordinary burst.");
}
