using System.Text;

namespace VeSessionManager.Web;

/// <summary>
/// Shared CSV writing for the app's export handlers — extracted from Job History's own private
/// helper when the VE Directory needed the same thing (issue #142), rather than copied. The
/// formula-injection rule below is exactly the sort of detail that gets fixed in one copy and not
/// the other.
/// </summary>
public static class CsvExport
{
    /// <summary>
    /// Quotes a field, and neutralises spreadsheet formula injection.
    ///
    /// <para>Excel and Sheets evaluate a cell beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or
    /// carriage return as a formula, so a leading apostrophe forces those to be read as text.
    /// <b>Quoting alone does not prevent it</b> — Excel strips the quotes and evaluates what is
    /// inside.</para>
    ///
    /// <para>This matters more for the VE export than it did for Job History. There the risky text
    /// was exception messages, which nobody chooses; here it is names, notes and addresses typed by
    /// people, and a VE directory is exactly the kind of file that gets mailed around a team.</para>
    /// </summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var first = value[0];
        if (first is '=' or '+' or '-' or '@' || first == (char)9 || first == (char)13)
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Joins pre-quoted fields into one row.</summary>
    public static string Row(params string?[] values) => string.Join(",", values.Select(Field));

    /// <summary>
    /// UTF-8 with a BOM, which is what makes Excel read the file as UTF-8 rather than mangling
    /// anything non-ASCII in a name.
    /// </summary>
    public static byte[] ToBytes(StringBuilder csv) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
}
