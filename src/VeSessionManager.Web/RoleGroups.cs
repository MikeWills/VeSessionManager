namespace VeSessionManager.Web;

/// <summary>
/// The role strings used by <c>[Authorize(Roles = …)]</c>, in one place (#307, DUP-07).
///
/// <para><b>Constants, because attribute arguments must be compile-time constant</b> — a static
/// readonly field will not compile in an attribute, which rules out building these from the
/// <c>UserRole</c> enum however much tidier that would read.</para>
///
/// <para>Named for what the group <i>is</i> rather than who is in it — <c>Admins</c>, not
/// <c>SystemAdminAndTeamAdmin</c> — so a page reads as "this is for admins", and changing the
/// membership later does not leave every call site named after the old one.</para>
/// </summary>
public static class RoleGroups
{
    /// <summary>Deployment-wide administration — shared reference data, system settings, VECs.</summary>
    public const string SystemAdminOnly = "SystemAdmin";

    /// <summary>Team administration: credentials, users, templates, the VE roster.</summary>
    public const string Admins = SystemAdminOnly + ",TeamAdmin";

    /// <summary>Anyone who runs sessions, plus the admins above.</summary>
    public const string SessionStaff = Admins + ",SessionManager";

    /// <summary>Every signed-in role, including read-only TeamLeads.</summary>
    public const string AllRoles = SessionStaff + ",TeamLead";
}
