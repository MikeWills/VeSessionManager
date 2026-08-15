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
    /// <summary>Guards against a pasted novel. Well above any real VE roster; low enough that the hidden-field round-trip stays sane.</summary>
    public const int MaxRows = 500;

    /// <param name="visibleTeamIds">
    /// Teams the person running the import can already see, used only to decide what the preview
    /// discloses about a cross-team match (#240). <b>Null means every team</b> — the SystemAdmin
    /// case, matching AdminAccessScope.GetEffectiveTeamIds' convention. It never changes which
    /// records are matched.
    /// </param>
    public async Task<VeImportPreview> ParseAsync(
        string csvText, int teamId, IReadOnlyList<int>? visibleTeamIds, CancellationToken cancellationToken)
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

            // Whether this match is a record the person running the import can already see anyway
            // (#240). null visibleTeamIds means every team — the SystemAdmin case, and the same
            // convention AdminAccessScope.GetEffectiveTeamIds uses.
            //
            // This changes ONLY what the preview renders. The match itself stays deployment-wide on
            // purpose: a VE already on another team's roster must be matched rather than duplicated,
            // which is the entire point of the person model (see docs/ve-management.md). Redacting
            // the match instead of the display would create duplicate people, which is a worse bug
            // than the one being fixed.
            var matchIsOutsideVisibleScope = match is not null
                && visibleTeamIds is not null
                && !match.TeamMemberships.Any(m => visibleTeamIds.Contains(m.TeamId));

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
                null)
            {
                SubmittedName = name,
                MatchIsOutsideVisibleScope = matchIsOutsideVisibleScope
            });
        }

        return new VeImportPreview(rows, null);
    }

    /// <summary>
    /// Applies a previously previewed parse. Takes the CSV text again rather than the preview object
    /// so the parse — and therefore every validation and match decision — is re-run server-side.
    /// </summary>
    public async Task<VeImportResult> ApplyAsync(string csvText, int teamId, int userId, CancellationToken cancellationToken)
    {
        // visibleTeamIds: null — the apply step renders nothing, so there is nothing to redact, and
        // the display members are not consulted here. Row.Name and Row.Action carry the truth.
        var preview = await ParseAsync(csvText, teamId, visibleTeamIds: null, cancellationToken);
        if (preview.Error is not null)
        {
            return new VeImportResult(0, 0, 0, preview.Rows.Count(r => !r.IsValid), preview.Error);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var created = 0;
        var updated = 0;
        var addedToTeam = 0;

        var validRows = preview.Rows.Where(r => r.IsValid).ToList();

        // One query for every matched person, instead of one per row (#294). ApplyRow used to
        // re-fetch each match with FirstAsync — which always round-trips, unlike FindAsync — and save
        // twice per row, so a 176-row import was roughly 500 round trips.
        //
        // Per-item durability is deliberately not the point here, so this is not the scan-job pattern
        // CLAUDE.md protects: the audit row was always written once at the end, so the operation was
        // never per-item atomic to begin with.
        var matchedIds = validRows
            .Where(r => r.MatchedVolunteerExaminerId is not null)
            .Select(r => r.MatchedVolunteerExaminerId!.Value)
            .Distinct()
            .ToList();

        var matchedPeople = matchedIds.Count == 0
            ? []
            : await dbContext.VolunteerExaminers
                .Include(v => v.TeamMemberships)
                .Where(v => matchedIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, cancellationToken);

        var applied = new List<(VeImportRow Row, VolunteerExaminer Person)>(validRows.Count);
        foreach (var row in validRows)
        {
            var (action, person) = ApplyRow(row, teamId, now, matchedPeople);
            applied.Add((row, person));
            if (action == VeImportAction.Create) created++;
            else if (action == VeImportAction.AddToTeam) addedToTeam++;
            else updated++;
        }

        dbContext.AddAuditLog(userId, "VeDirectoryImported", nameof(VolunteerExaminer), 0,
            $"Imported {created} new VE(s), updated {updated}, added {addedToTeam} existing VE(s) to the team.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // After the save, not inside the loop: a newly created person has Id 0 until then.
        foreach (var (row, person) in applied)
        {
            row.AppliedVolunteerExaminerId = person.Id;
        }

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
    /// <param name="matchedPeople">
    /// Every person this call might match, loaded once by the caller <b>with TeamMemberships</b>.
    /// The include is not optional: the membership guard below reads that collection, and a person
    /// loaded without it looks like they hold no memberships at all, so the import would hand them a
    /// duplicate membership on a team they are already on.
    /// </param>
    private (VeImportAction Action, VolunteerExaminer Person) ApplyRow(
        VeImportRow row, int teamId, DateTime now, IReadOnlyDictionary<int, VolunteerExaminer> matchedPeople)
    {
        var person = row.MatchedVolunteerExaminerId is { } matchedId
            ? matchedPeople[matchedId]
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

        // No save here — the caller saves once for the whole batch. The membership is attached
        // through the navigation rather than by setting VolunteerExaminerId, because a person created
        // moments ago still has Id 0; EF fills the foreign key in from the relationship on save.
        //
        // Adding to the tracked collection is also what keeps the guard correct without the
        // save-per-row it replaced. ParseAsync rejects rows sharing a call sign or name outright
        // ("Appears more than once in this file"), but two *different* keys can still resolve to one
        // person — one row matched on FRN, another on call sign. Both then hold the same instance
        // out of matchedPeople, so the second sees the membership the first added rather than
        // needing a database round trip to find out.
        if (!person.TeamMemberships.Any(m => m.TeamId == teamId))
        {
            person.TeamMemberships.Add(new VeTeamMembership
            {
                Team = null!,
                VolunteerExaminer = person,
                TeamId = teamId,
                IsActive = true,
                CreatedUtc = now
            });
        }

        return (action, person);
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

        // The match is already loaded here, with TeamMemberships, so it is handed straight to
        // ApplyRow rather than re-fetched. One row, so the dictionary holds at most one entry.
        var matchedPeople = match is null
            ? new Dictionary<int, VolunteerExaminer>()
            : new Dictionary<int, VolunteerExaminer> { [match.Id] = match };

        var (action, person) = ApplyRow(row, teamId, now, matchedPeople);
        await dbContext.SaveChangesAsync(cancellationToken);
        row.AppliedVolunteerExaminerId = person.Id;

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

    /// <summary>The name as it appeared in the file, or null if the file supplied none. Distinct from
    /// <see cref="Name"/>, which falls back to a matched record's name so the apply step does not
    /// overwrite a real name with a call sign.</summary>
    public string? SubmittedName { get; init; }

    /// <summary>This row matched a VE the importer cannot otherwise see — see the display members below (#240).</summary>
    public bool MatchIsOutsideVisibleScope { get; init; }

    /// <summary>
    /// What the preview shows, as opposed to what the apply step does (#240).
    ///
    /// <para>Uploading 500 call signs with no <c>Name</c> column and stopping at the preview was an
    /// existence-and-name oracle over the whole deployment: every <c>AddToTeam</c> row meant "this
    /// person exists on some team that is not yours", and <see cref="Name"/> rendered *their* record's
    /// name. Read-only, 500 probes a request, and unaudited — <c>VeDirectoryImported</c> is only
    /// written on apply.</para>
    ///
    /// <para>So the preview echoes back what was submitted, and reports the row as <c>Create</c>,
    /// which is what it looks like from where the importer is standing. <see cref="Name"/> and
    /// <see cref="Action"/> keep the truth for the apply step, which still matches deployment-wide
    /// and still adds a membership rather than duplicating the person.</para>
    /// </summary>
    public string DisplayName => MatchIsOutsideVisibleScope ? SubmittedName ?? CallSign ?? Name : Name;

    /// <inheritdoc cref="DisplayName"/>
    public VeImportAction DisplayAction =>
        MatchIsOutsideVisibleScope && Action == VeImportAction.AddToTeam ? VeImportAction.Create : Action;

    public static VeImportRow Invalid(int lineNumber, string? callSign, string? name, string problem) =>
        new(lineNumber, callSign, name ?? "", null, null, null, null, null, null, null, null, null, null, VeImportAction.Invalid, problem);
}

/// <param name="Error">Set when the file could not be read at all, in which case no row is actionable.</param>
public record VeImportPreview(IReadOnlyList<VeImportRow> Rows, string? Error)
{
    // DisplayAction, not Action: a summary reading "3 will be added to the team" is the same
    // existence oracle as the per-row badge, just aggregated (#240). On the apply path every row's
    // DisplayAction equals its Action, so these are unchanged there.
    public int CreateCount => Rows.Count(r => r.DisplayAction == VeImportAction.Create);
    public int UpdateCount => Rows.Count(r => r.DisplayAction == VeImportAction.Update);
    public int AddToTeamCount => Rows.Count(r => r.DisplayAction == VeImportAction.AddToTeam);
    public int InvalidCount => Rows.Count(r => !r.IsValid);
}

public record VeImportResult(int Created, int Updated, int AddedToTeam, int Skipped, string? Error);

/// <param name="VolunteerExaminerId">The person written, for linking straight to them.</param>
/// <param name="Action">Whether they were created, added to this team, or already here.</param>
/// <param name="Error">Set when nothing was written.</param>
public record VeAddResult(int? VolunteerExaminerId, VeImportAction? Action, string? Error);
