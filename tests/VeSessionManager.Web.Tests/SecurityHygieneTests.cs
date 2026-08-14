using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The behavioural half of the low-severity hygiene sweep (#312). Items that were documentation
/// decisions (L-03, L-08) or pure annotations are not represented here; these are the ones where
/// something an HTTP caller can observe changed.
/// </summary>
public class SecurityHygieneTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public SecurityHygieneTests(WebAppFactory factory) => _factory = factory;

    /// <summary>
    /// L-11. The VE directory export writes a <c>VeDirectoryExported</c> audit row attesting that a
    /// copy of every VE's contact details left the building. As a GET, a cross-site link or an
    /// <c>&lt;img src&gt;</c> could make an authenticated admin emit that row from someone else's
    /// page — no PII leaked, since a cross-origin caller cannot read the body, but the one record
    /// that exists to attest who exported PII became forgeable.
    ///
    /// <para>Note what a GET now does: Razor Pages falls back to the default handler for an unknown
    /// one, so the page still renders 200. That is fine and is why the assertion is about the CSV
    /// and the audit row rather than the status code — the first version of this test checked for a
    /// non-200 and failed against a correct fix.</para>
    /// </summary>
    [Fact]
    public async Task VeDirectoryExport_ByGet_ProducesNoCsvAndNoAuditRow()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var before = await ExportAuditRowCountAsync();

        var response = await client.GetAsync("/SessionManager/VeDirectory?handler=Export");

        Assert.NotEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(before, await ExportAuditRowCountAsync());
    }

    /// <summary>The other half — the real button must still work, and must still audit.</summary>
    [Fact]
    public async Task VeDirectoryExport_ByPost_StillExportsAndAudits()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var before = await ExportAuditRowCountAsync();

        var page = await client.GetStringAsync("/SessionManager/VeDirectory");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var response = await client.PostAsync("/SessionManager/VeDirectory?handler=Export",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(before + 1, await ExportAuditRowCountAsync());
    }

    private async Task<int> ExportAuditRowCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.CountAsync(a => a.Action == "VeDirectoryExported");
    }

    /// <summary>
    /// L-01. Nothing reads the filter cookie from JavaScript, so HttpOnly costs nothing. Secure is
    /// deliberately conditional on the request scheme — hardcoding it would make the filter row
    /// silently forget itself over http://localhost with nothing to explain why.
    /// </summary>
    [Fact]
    public async Task SessionFilterCookie_IsHttpOnly()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await client.GetAsync("/SessionManager?applied=true&pageSize=25");

        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("vsm_session_filters", StringComparison.Ordinal))
            : null;

        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        // Not asserting Secure: these requests are http, so the conditional correctly omits it.
        // Asserting its absence here is what proves the condition is real rather than hardcoded.
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }
}
