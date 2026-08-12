using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Every field a form submits must bind to something on its page model (issue #269).
///
/// <para><b>GET forms count.</b> The obvious scope is POST, and the first version of this test used
/// it — and passed with #253 reintroduced verbatim, because #253 was in a GET form. Both verbs are
/// scanned now. A GET form has a second way to fail besides a wrong name: a plain
/// <c>[BindProperty]</c> without <c>SupportsGet</c> does not bind on GET at all.</para>
///
/// <para><b>Why this is a source scan and not a request test.</b> The failure is silent by
/// construction: a <c>name=</c> that matches no bound property or handler parameter does not error,
/// it binds the default — <c>null</c>, <c>0</c>, <c>false</c> — and the handler carries on with a
/// value the user never chose. Nothing throws, nothing logs, and the page still redirects with a
/// success message. Only comparing the markup against the model can see it.</para>
///
/// <para><b>The gap it closes.</b> ~110 form-posting handlers exist and one is covered by a request
/// test. The 2026-08-11 audit found three live binding defects by reading pages by hand; this one
/// catches the first statically — verified by reintroducing it and watching this fail:</para>
///
/// <list type="bullet">
///   <item><b>#253</b> — the session list posted <c>name="sortDirection"</c> at a property bound as
///   <c>Name = "dir"</c>, so applying any filter silently discarded the user's column sort.</item>
///   <item><b>#276</b> — VeDetail's forms drop the query string. <i>Not</i> caught here: the names
///   are all correct, the values simply never arrive. Noted so the limit is on the record.</item>
///   <item><b>#277</b> — an <c>int?</c> rendered into a non-nullable <c>int</c> parameter. Also not
///   caught: the name matches, only the type is wrong when the value is absent.</item>
/// </list>
///
/// <para>Same shape and rationale as Core's <c>InlineEventHandlerTests</c> and the Worker's
/// <c>JobRegistrationTests</c>: the mistake is someone writing — or failing to write — one line, and
/// the compiler has no opinion about any of it.</para>
/// </summary>
public class FormBindingTests
{
    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!;
    }

    /// <summary>Shared with the other source-scanning tests in this project.</summary>
    internal static string RepositoryRootPath() => RepositoryRoot().FullName;

    private static string PagesRoot() =>
        Path.Combine(RepositoryRoot().FullName, "src", "VeSessionManager.Web", "Pages");

    /// <summary>A whole <c>&lt;form&gt;</c> element, including its contents.</summary>
    private static readonly Regex FormBlock = new(
        @"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex HandlerFromTagHelper = new(
        @"asp-page-handler=""(?<handler>[A-Za-z]+)""", RegexOptions.Compiled);

    /// <summary>
    /// <c>action="@Model.BuildActionUrl("Handler")"</c> — the helper that exists so a filtered list
    /// page keeps its query string, which <c>asp-page-handler</c> drops.
    /// </summary>
    private static readonly Regex HandlerFromBuildActionUrl = new(
        @"action=""@(?:Model\.)?\w+\(\s*""(?<handler>[A-Za-z]+)""", RegexOptions.Compiled);

    /// <summary>
    /// <c>action="@Url.Page("/Some/Page", "Handler", …)"</c> — same purpose, but the handler is the
    /// <b>second</b> argument, after the page path.
    /// </summary>
    private static readonly Regex HandlerFromUrlPage = new(
        @"Url\.Page\(\s*""[^""]*""\s*,\s*""(?<handler>[A-Za-z]+)""", RegexOptions.Compiled);

    private static readonly Regex FieldName = new(
        @"\bname=""(?<name>[^""@\s]+)""", RegexOptions.Compiled);

    /// <summary><c>asp-for="Input.Email"</c> renders <c>name="Input.Email"</c>; only the root matters for binding.</summary>
    private static readonly Regex AspFor = new(
        @"\basp-for=""(?<expr>[A-Za-z_][A-Za-z0-9_.]*)""", RegexOptions.Compiled);

    /// <summary>
    /// A Razor comment. Stripped before anything else looks at the file, because these pages explain
    /// their own markup in them — <c>VeTags.cshtml</c> has a comment containing the literal text
    /// <c>&lt;form&gt;</c>, which the block regex above happily matched, swallowing the real form
    /// thirty lines below it along with every field in between.
    /// </summary>
    private static readonly Regex RazorComment = new(
        @"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// An input that names a form by id — <c>&lt;input form="tag-@row.Tag.Id" name="sortOrder" …&gt;</c>.
    /// HTML lets a field live outside the form it submits to, and this app uses that deliberately: an
    /// editable table row cannot wrap its cells in a form without nesting one inside another. Without
    /// this those fields are simply invisible to the scan, which is the quiet kind of gap — the test
    /// passes and covers less than it claims.
    /// </summary>
    private static readonly Regex DetachedField = new(
        @"<(?:input|select|textarea)\b[^>]*\bform=""(?<form>[^""]+)""[^>]*>", RegexOptions.Compiled);

    private static readonly Regex FormId = new(@"\bid=""(?<id>[^""]+)""", RegexOptions.Compiled);

    /// <summary>Framework-supplied, never declared on a page model.</summary>
    private static readonly HashSet<string> AlwaysBound = new(StringComparer.OrdinalIgnoreCase)
    {
        "__RequestVerificationToken"
    };

    /// <param name="Handlers">
    /// Every handler this form can post to. Usually one, but a form may carry several submit buttons
    /// that each name their own via <c>asp-page-handler</c> — the sign-in page's external-provider
    /// buttons do exactly that, and the field they carry (<c>name="provider"</c>) belongs to the
    /// handler on the button rather than to the form. Empty means the unnamed <c>OnPostAsync</c>.
    /// </param>
    /// <param name="IsPost">
    /// <c>method="post"</c>. GET forms are in scope too and for a specific reason: #253, the bug that
    /// motivated this test, was in one. A GET form additionally binds only to properties marked
    /// <c>SupportsGet</c>, so it has a second way to fail that a POST form does not.
    /// </param>
    private sealed record Form(
        string Page, IReadOnlyList<string> Handlers, string[] Names, string Attrs, int Line, bool IsPost)
    {
        public string Verb => IsPost ? "Post" : "Get";

        public string HandlerLabel => Handlers.Count == 0 ? "(default)" : string.Join("/", Handlers);
    }

    private static IEnumerable<Form> FormsIn(string path)
    {
        // Blanked rather than removed, so every line number still points where it did in the file.
        var text = RazorComment.Replace(File.ReadAllText(path),
            m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));
        var relative = Path.GetRelativePath(PagesRoot(), path);

        // Fields that name their form by id, gathered once and handed to whichever form claims that id.
        var detached = DetachedField.Matches(text)
            .GroupBy(m => m.Groups["form"].Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(m => m.Value)), StringComparer.Ordinal);

        foreach (Match match in FormBlock.Matches(text))
        {
            var attrs = match.Groups["attrs"].Value;
            var isPost = attrs.Contains("method=\"post\"", StringComparison.OrdinalIgnoreCase);
            var body = match.Groups["body"].Value;

            var id = FormId.Match(attrs);
            if (id.Success && detached.TryGetValue(id.Groups["id"].Value, out var outside))
            {
                body += " " + outside;
            }

            // Form-level first, then any per-button handlers inside it. Both are real: the form
            // attribute is the common case, a button carrying its own is how a multi-submit form
            // routes to different handlers from one set of fields.
            var handlers = new List<string>();
            foreach (var source in new[] { attrs, body })
            {
                handlers.AddRange(HandlerFromTagHelper.Matches(source).Select(m => m.Groups["handler"].Value));
            }

            handlers.AddRange(HandlerFromBuildActionUrl.Matches(attrs).Select(m => m.Groups["handler"].Value));
            handlers.AddRange(HandlerFromUrlPage.Matches(attrs).Select(m => m.Groups["handler"].Value));
            var names = FieldName.Matches(body).Select(m => m.Groups["name"].Value)
                .Concat(AspFor.Matches(body).Select(m => m.Groups["expr"].Value.Split('.')[0]))
                .Where(n => !AlwaysBound.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var line = text.Take(match.Index).Count(c => c == '\n') + 1;
            yield return new Form(relative, [.. handlers.Distinct(StringComparer.OrdinalIgnoreCase)], names, attrs, line, isPost);
        }
    }

    /// <summary><c>Pages/Admin/Teams.cshtml</c> → <c>…Pages.Admin.TeamsModel</c>.</summary>
    private static Type? PageModelFor(string relativePagePath)
    {
        var withoutExtension = relativePagePath[..^".cshtml".Length].Replace('\\', '.').Replace('/', '.');
        var expected = $"VeSessionManager.Web.Pages.{withoutExtension}Model";
        return typeof(Program).Assembly.GetType(expected);
    }

    /// <summary>
    /// Names the model will accept: bound properties plus the parameters of the handlers this form can
    /// reach.
    ///
    /// <para>Two rules do the work, and both are #253. <b>The renamed property</b> —
    /// <c>[BindProperty(Name = "dir")] SortDirection</c> accepts <c>dir</c> and <i>not</i>
    /// <c>sortDirection</c>, so the bind name is used and the C# name is not. <b>SupportsGet</b> — a
    /// plain <c>[BindProperty]</c> does not bind on GET at all, so for a GET form only the
    /// <c>SupportsGet</c> ones count.</para>
    /// </summary>
    private static HashSet<string> BindableNames(Type pageModel, IReadOnlyList<string> handlers, bool isPost)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in pageModel.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var bind = property.GetCustomAttribute<BindPropertyAttribute>();
            if (bind is not null && (isPost || bind.SupportsGet))
            {
                names.Add(bind.Name ?? property.Name);
            }
        }

        // A handler's parameters are only in scope for the handlers this form can actually reach,
        // which is the point: a field posted to Create must not be satisfied by a parameter that only
        // Update declares. A multi-submit form legitimately reaches several, so their parameters are
        // unioned — the markup cannot say which button was pressed.
        foreach (var handler in handlers.Count == 0 ? [""] : handlers)
        {
            foreach (var suffix in new[] { "Async", "" })
            {
                var method = pageModel.GetMethod($"On{(isPost ? "Post" : "Get")}{handler}{suffix}",
                    BindingFlags.Public | BindingFlags.Instance);
                if (method is null)
                {
                    continue;
                }

                foreach (var parameter in method.GetParameters())
                {
                    names.Add(parameter.Name!);
                }

                break;
            }
        }

        return names;
    }

    private static List<string> CshtmlFiles() =>
        [.. Directory.EnumerateFiles(PagesRoot(), "*.cshtml", SearchOption.AllDirectories)
            // Shared partials and layouts have no page model of their own; the logout form in
            // _AppLayout posts to /Account/Logout, which this cannot resolve from the markup.
            .Where(f => !Path.GetFileName(f).StartsWith('_'))];

    [Fact]
    public void EveryPostedFieldBindsToSomethingOnItsPageModel()
    {
        var offenders = new List<string>();

        foreach (var file in CshtmlFiles())
        {
            foreach (var form in FormsIn(file))
            {
                var pageModel = PageModelFor(form.Page);
                if (pageModel is null)
                {
                    continue; // covered by its own test below
                }

                var bindable = BindableNames(pageModel, form.Handlers, form.IsPost);

                foreach (var name in form.Names.Where(n => !bindable.Contains(n)))
                {
                    offenders.Add(
                        $"{form.Page}:{form.Line} — submits name=\"{name}\" to handler " +
                        $"On{form.Verb}{form.HandlerLabel}Async, which has no matching " +
                        $"[BindProperty] or parameter on {pageModel.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A posted field that binds to nothing does not error — it binds null/0/false and the handler " +
            "carries on with a value the user never chose:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every named handler a form targets must exist. A typo here is a 400 from the framework rather
    /// than a silent default, so it is louder than the above — but still nothing the compiler sees.
    /// </summary>
    [Fact]
    public void EveryFormTargetsAHandlerThatExists()
    {
        var offenders = new List<string>();

        foreach (var file in CshtmlFiles())
        {
            foreach (var form in FormsIn(file).SelectMany(f => f.Handlers.Select(h => (Form: f, Handler: h))).Select(x => x.Form with { Handlers = [x.Handler] }))
            {
                var pageModel = PageModelFor(form.Page);
                if (pageModel is null)
                {
                    offenders.Add($"{form.Page}:{form.Line} — no page model type found for this page");
                    continue;
                }

                var exists = pageModel.GetMethod($"On{form.Verb}{form.Handlers[0]}Async", BindingFlags.Public | BindingFlags.Instance) is not null
                    || pageModel.GetMethod($"On{form.Verb}{form.Handlers[0]}", BindingFlags.Public | BindingFlags.Instance) is not null;

                if (!exists)
                {
                    offenders.Add($"{form.Page}:{form.Line} — targets On{form.Verb}{form.Handlers[0]}Async, which does not exist on {pageModel.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The documented trap: <c>FormTagHelper</c> only auto-emits the antiforgery token when it
    /// generated the action itself. Give a form an explicit <c>action=</c> — which the list pages must,
    /// because <c>asp-page-handler</c> drops the query string — and the token disappears, so every POST
    /// 400s in middleware <b>before reaching the app, logging nothing</b>. The symptom is a browser
    /// error page and a completely silent server log.
    /// </summary>
    [Fact]
    public void EveryFormWithAnExplicitActionAlsoRequestsTheAntiforgeryToken()
    {
        var offenders = new List<string>();

        foreach (var file in CshtmlFiles())
        {
            foreach (var form in FormsIn(file))
            {
                var hasExplicitAction = form.Attrs.Contains("action=", StringComparison.OrdinalIgnoreCase);
                var asksForToken = form.Attrs.Contains("asp-antiforgery=\"true\"", StringComparison.OrdinalIgnoreCase);

                if (hasExplicitAction && !asksForToken)
                {
                    offenders.Add($"{form.Page}:{form.Line} — explicit action= without asp-antiforgery=\"true\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "An explicit action= suppresses the auto-emitted antiforgery token, and every POST then 400s " +
            "in middleware before reaching the app — with nothing in the server log:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Non-vacuity. Every assertion above is "this list is empty", which passes trivially if the
    /// parser finds nothing — a regex that silently stopped matching would look like a clean bill of
    /// health.
    /// </summary>
    [Fact]
    public void TheParserActuallyFindsFormsFieldsAndHandlers()
    {
        var forms = CshtmlFiles().SelectMany(FormsIn).ToList();

        Assert.True(forms.Count > 90, $"only found {forms.Count} forms — the parser has probably stopped matching");
        Assert.True(forms.Count(f => f.IsPost) > 80, "found almost no POST forms");
        Assert.True(forms.Count(f => !f.IsPost) > 5, "found no GET forms — #253 was in one");
        Assert.True(forms.Count(f => f.Handlers.Count > 0) > 70, "found almost no named handlers");
        Assert.True(forms.Sum(f => f.Names.Length) > 200, "found almost no submitted fields");
        Assert.Contains(forms, f => f.Page.Contains("CandidateDetail"));

        // The two shapes whose absence would be silent rather than loud: a form the parser can only
        // see once Razor comments are blanked, and one whose fields live outside it.
        var veTags = forms.Where(f => f.Page.Contains("VeTags") && f.Handlers.Contains("Update")).ToList();
        Assert.Single(veTags);
        Assert.Contains("sortOrder", veTags[0].Names);
    }
}
