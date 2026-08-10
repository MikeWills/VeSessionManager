using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Bulk-adding VEs from a CSV (issue #142 phase 4).
///
/// <para><b>Preview and apply run the same parse.</b> The confirm step re-parses the identical text
/// rather than trusting a structure the browser posts back, so the two cannot drift apart and there
/// is no per-row client-editable payload to police. What was previewed is what applies.</para>
///
/// <para><b>A blank cell means "no opinion", never "delete".</b> An import that silently emptied a
/// phone number because the spreadsheet someone built happened to omit that column would be a much
/// worse outcome than one that ignores it — and it is the kind of loss nobody notices until they
/// need the number.</para>
///
/// <para><b>Import is the other duplicate-generating path.</b> Matching therefore uses the same
/// identity rules as the sync: FRN first when the file supplies one, then a usable call sign, and
/// never a placeholder. A file listing two rows with the same call sign is an error on the second,
/// not a silent overwrite of the first.</para>
/// </summary>
public class VolunteerExaminerImportService(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Columns the parser understands, in the order the export writes them. Anything else in the header is ignored rather than rejected — a team's own spreadsheet will have extra columns.</summary>
    public static readonly IReadOnlyList<string> KnownColumns =
        ["callsign", "name", "email", "phone", "addressline1", "addressline2", "city", "state", "postalcode", "discord", "frn"];

    /// <summary>Guards against a pasted novel. Well above any real VE roster; low enough that the hidden-field round-trip stays sane.</summary>
    public const int MaxRows = 500;

    public async Task<VeImportPreview> ParseAsync(string csvText, int teamId, CancellationToken cancellationToken)
    {
        var rows = new List<VeImportRow>();
        var lines = SplitLines(csvText);

        if (lines.Count == 0)
        {
            return new VeImportPreview(rows, "The file is empty.");
        }

        var header = ParseLine(lines[0]).Select(h => h.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "")).ToList();
        var callSignColumn = header.IndexOf("callsign");
        var nameColumn = header.IndexOf("name");

        if (callSignColumn < 0 && nameColumn < 0)
        {
            return new VeImportPreview(rows, "No 'CallSign' or 'Name' column found. The first row must be a header.");
        }

        if (lines.Count - 1 > MaxRows)
        {
            return new VeImportPreview(rows, $"That file has {lines.Count - 1} rows; the limit is {MaxRows}.");
        }

        // Existing people, unfiltered by team: a VE already on another team's roster must be matched,
        // not duplicated — the whole point of the person model.
        var existing = await dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships)
            .ToListAsync(cancellationToken);

        var byCallSign = existing
            .Where(v => CallSign.IsUsable(v.CallSign))
            .ToDictionary(v => v.CallSign!, StringComparer.OrdinalIgnoreCase);
        var byFrn = existing
            .Where(v => v.Frn is not null)
            .GroupBy(v => v.Frn!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < lines.Count; i++)
        {
            var fields = ParseLine(lines[i]);
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? Value(string column)
            {
                var index = header.IndexOf(column);
                if (index < 0 || index >= fields.Count) return null;
                return string.IsNullOrWhiteSpace(fields[index]) ? null : fields[index].Trim();
            }

            var rawCallSign = Value("callsign");
            var callSign = CallSign.Normalize(rawCallSign);
            var name = Value("name");
            var frn = Value("frn");

            if (name is null && callSign is null)
            {
                rows.Add(VeImportRow.Invalid(i + 1, rawCallSign, name, "Needs at least a name or a usable call sign."));
                continue;
            }

            if (rawCallSign is not null && callSign is null)
            {
                // "<UNKNOWN>" and friends. Importing one would create a person nobody can identify
                // and that no license check can ever resolve.
                rows.Add(VeImportRow.Invalid(i + 1, rawCallSign, name, $"'{rawCallSign}' is not a usable call sign."));
                continue;
            }

            var fileKey = callSign ?? $"name:{name!.ToLowerInvariant()}";
            if (!seenInFile.Add(fileKey))
            {
                rows.Add(VeImportRow.Invalid(i + 1, callSign, name, "Appears more than once in this file."));
                continue;
            }

            VolunteerExaminer? match = null;
            if (frn is not null && byFrn.TryGetValue(frn, out var byFrnMatch))
            {
                match = byFrnMatch;
            }
            else if (callSign is not null && byCallSign.TryGetValue(callSign, out var byCallSignMatch))
            {
                match = byCallSignMatch;
            }

            var alreadyOnTeam = match?.TeamMemberships.Any(m => m.TeamId == teamId) ?? false;

            rows.Add(new VeImportRow(
                i + 1,
                callSign,
                name ?? match?.Name ?? callSign!,
                Value("email"),
                Value("phone"),
                Value("addressline1"),
                Value("addressline2"),
                Value("city"),
                Value("state"),
                Value("postalcode"),
                Value("discord"),
                frn,
                match?.Id,
                match is null ? VeImportAction.Create : alreadyOnTeam ? VeImportAction.Update : VeImportAction.AddToTeam,
                null));
        }

        return new VeImportPreview(rows, null);
    }

    /// <summary>
    /// Applies a previously previewed parse. Takes the CSV text again rather than the preview object
    /// so the parse — and therefore every validation and match decision — is re-run server-side.
    /// </summary>
    public async Task<VeImportResult> ApplyAsync(string csvText, int teamId, int userId, CancellationToken cancellationToken)
    {
        var preview = await ParseAsync(csvText, teamId, cancellationToken);
        if (preview.Error is not null)
        {
            return new VeImportResult(0, 0, 0, preview.Rows.Count(r => !r.IsValid), preview.Error);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var created = 0;
        var updated = 0;
        var addedToTeam = 0;

        foreach (var row in preview.Rows.Where(r => r.IsValid))
        {
            var action = await ApplyRowAsync(row, teamId, now, cancellationToken);
            if (action == VeImportAction.Create) created++;
            else if (action == VeImportAction.AddToTeam) addedToTeam++;
            else updated++;
        }

        dbContext.AddAuditLog(userId, "VeDirectoryImported", nameof(VolunteerExaminer), 0,
            $"Imported {created} new VE(s), updated {updated}, added {addedToTeam} existing VE(s) to the team.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new VeImportResult(created, updated, addedToTeam, preview.Rows.Count(r => !r.IsValid), null);
    }

    /// <summary>
    /// Writes one resolved row: create the person, or fill their blanks, and make sure they hold a
    /// membership on this team.
    ///
    /// <para><b>Shared by the CSV import and the single manual add on purpose.</b> Both are
    /// duplicate-generating paths into the same table, and the moment they each own a copy of
    /// "match, then create-or-fill, then ensure membership" the copies drift — which is exactly how
    /// the per-team refresh pipeline went wrong before <c>TeamPipeline</c> existed.</para>
    /// </summary>
    private async Task<VeImportAction> ApplyRowAsync(VeImportRow row, int teamId, DateTime now, CancellationToken cancellationToken)
    {
        var person = row.MatchedVolunteerExaminerId is { } matchedId
            ? await dbContext.VolunteerExaminers
                .Include(v => v.TeamMemberships)
                .FirstAsync(v => v.Id == matchedId, cancellationToken)
            : null;

        var action = person is null ? VeImportAction.Create : row.Action;

        if (person is null)
        {
            person = new VolunteerExaminer { Name = row.Name, CallSign = row.CallSign, CreatedUtc = now };
            dbContext.VolunteerExaminers.Add(person);
        }

        // Blank means "no opinion", never "delete". Only values actually supplied are written, and a
        // name is only replaced when there is one to replace it with.
        if (!string.IsNullOrWhiteSpace(row.Name)) person.Name = row.Name;
        person.Email ??= row.Email;
        person.Phone ??= row.Phone;
        person.AddressLine1 ??= row.AddressLine1;
        person.AddressLine2 ??= row.AddressLine2;
        person.City ??= row.City;
        person.State ??= row.State;
        person.PostalCode ??= row.PostalCode;
        person.DiscordUsername ??= row.Discord;
        person.UpdatedUtc = now;

        // FRN is never taken from a spreadsheet or a form. It is the identity key, it is unique, and
        // the ULS sweep already fills it from FCC — accepting a typo here would either collide with
        // a real person's record or quietly attach the wrong identity to this one.

        await dbContext.SaveChangesAsync(cancellationToken);

        if (!person.TeamMemberships.Any(m => m.TeamId == teamId))
        {
            var membership = new VeTeamMembership
            {
                VolunteerExaminerId = person.Id,
                TeamId = teamId,
                IsActive = true,
                CreatedUtc = now
            };
            person.TeamMemberships.Add(membership);
            dbContext.VeTeamMemberships.Add(membership);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        row.AppliedVolunteerExaminerId = person.Id;
        return action;
    }

    /// <summary>
    /// Add <b>one</b> VE by hand (requested 2026-08-10) — someone a team is watching before they ever
    /// work a session.
    ///
    /// <para><b>This is not a duplicate of something ExamTools does</b>, which is the test every
    /// in-app admin action here has to pass (three were built and removed for failing it). ExamTools
    /// only knows a VE once they are rostered onto a session; a prospect being monitored to join has
    /// never worked one, so ingestion will never produce them. Nothing else can create this row.</para>
    ///
    /// <para>Matching is the importer's, unchanged: an existing person on this team is left alone, and
    /// one already serving another team gains a membership here rather than a second record. Adding
    /// someone who later turns up on a real ExamTools roster is therefore safe — the sync matches the
    /// same way and finds this record instead of creating a rival.</para>
    /// </summary>
    public async Task<VeAddResult> AddOneAsync(
        int teamId, string? callSign, string? name, string? email, string? phone, int userId, CancellationToken cancellationToken)
    {
        var normalized = CallSign.Normalize(callSign);
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (!string.IsNullOrWhiteSpace(callSign) && normalized is null)
        {
            return new VeAddResult(null, null, $"'{callSign.Trim()}' is not a usable call sign.");
        }

        if (name is null && normalized is null)
        {
            return new VeAddResult(null, null, "Enter a name or a call sign.");
        }

        var existing = await dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships)
            .ToListAsync(cancellationToken);

        var match = normalized is null
            ? null
            : existing.FirstOrDefault(v => CallSign.IsUsable(v.CallSign)
                && string.Equals(v.CallSign, normalized, StringComparison.OrdinalIgnoreCase));

        var alreadyOnTeam = match?.TeamMemberships.Any(m => m.TeamId == teamId) ?? false;

        var row = new VeImportRow(
            0, normalized, name ?? match?.Name ?? normalized!,
            string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            null, null, null, null, null, null, null,
            match?.Id,
            match is null ? VeImportAction.Create : alreadyOnTeam ? VeImportAction.Update : VeImportAction.AddToTeam,
            null);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var action = await ApplyRowAsync(row, teamId, now, cancellationToken);

        var who = normalized ?? row.Name;
        dbContext.AddAuditLog(userId, "VeAddedByHand", nameof(VolunteerExaminer), row.AppliedVolunteerExaminerId ?? 0,
            action switch
            {
                VeImportAction.Create => $"VE {who} added by hand.",
                VeImportAction.AddToTeam => $"Existing VE {who} added to this team by hand.",
                _ => $"VE {who} was already on this team; details filled in."
            },
            now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new VeAddResult(row.AppliedVolunteerExaminerId, action, null);
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Length > 0 || false)];

    /// <summary>
    /// Minimal RFC 4180 field splitter — quoted fields, doubled quotes inside them, commas within
    /// quotes. Hand-rolled rather than a package: the format this reads is the one this app writes,
    /// and a dependency for twenty lines needs asking about first.
    /// </summary>
    private static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());

        // Undo the export's formula-injection guard so a round trip is lossless — a name that went
        // out as "'=Smith" must come back as "=Smith", not gain an apostrophe on every cycle.
        return [.. fields.Select(f => f.StartsWith('\'') ? f[1..] : f)];
    }
}

public enum VeImportAction
{
    Create,

    /// <summary>Known person, already on this team — their blank fields get filled.</summary>
    Update,

    /// <summary>Known person serving another team. They gain a membership here rather than a second record.</summary>
    AddToTeam,

    Invalid
}

public record VeImportRow(
    int LineNumber,
    string? CallSign,
    string Name,
    string? Email,
    string? Phone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? Discord,
    string? Frn,
    int? MatchedVolunteerExaminerId,
    VeImportAction Action,
    string? Problem)
{
    public bool IsValid => Action != VeImportAction.Invalid;

    /// <summary>Set by the apply step so the caller can link to the person it just wrote. Not part of the parse.</summary>
    public int? AppliedVolunteerExaminerId { get; set; }

    public static VeImportRow Invalid(int lineNumber, string? callSign, string? name, string problem) =>
        new(lineNumber, callSign, name ?? "", null, null, null, null, null, null, null, null, null, null, VeImportAction.Invalid, problem);
}

/// <param name="Error">Set when the file could not be read at all, in which case no row is actionable.</param>
public record VeImportPreview(IReadOnlyList<VeImportRow> Rows, string? Error)
{
    public int CreateCount => Rows.Count(r => r.Action == VeImportAction.Create);
    public int UpdateCount => Rows.Count(r => r.Action == VeImportAction.Update);
    public int AddToTeamCount => Rows.Count(r => r.Action == VeImportAction.AddToTeam);
    public int InvalidCount => Rows.Count(r => !r.IsValid);
}

public record VeImportResult(int Created, int Updated, int AddedToTeam, int Skipped, string? Error);

/// <param name="VolunteerExaminerId">The person written, for linking straight to them.</param>
/// <param name="Action">Whether they were created, added to this team, or already here.</param>
/// <param name="Error">Set when nothing was written.</param>
public record VeAddResult(int? VolunteerExaminerId, VeImportAction? Action, string? Error);
