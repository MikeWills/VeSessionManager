using VeSessionManager.Core.ExamTools;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #38: export/full.json's VE list is wrapped under a "devdoc" key on dev (examtools.dev) but
/// NOT wrapped at all on prod (alpha.exam.tools, confirmed live 2026-07-29 against real HRCC data) —
/// VolunteerExaminerSyncService silently found zero VEs for every real HRCC session because of this.
/// ExamToolsFullExport.ResolveVes() is the fix; these tests pin both real shapes plus the neither-present case.
/// </summary>
public class ExamToolsFullExportTests
{
    [Fact]
    public void DevWrappedShape_ResolvesFromDevdoc()
    {
        var export = new ExamToolsFullExport
        {
            Devdoc = new ExamToolsFullExportDevDoc { Ves = [new ExamToolsVe { Call = "N2SPG", Name = "Test VE" }] }
        };

        var result = export.ResolveVes();

        var ve = Assert.Single(result);
        Assert.Equal("N2SPG", ve.Call);
    }

    [Fact]
    public void ProdUnwrappedShape_ResolvesFromTopLevelVes()
    {
        var export = new ExamToolsFullExport
        {
            Devdoc = null,
            Ves = [new ExamToolsVe { Call = "W5CBW", Name = "Craig Wall" }]
        };

        var result = export.ResolveVes();

        var ve = Assert.Single(result);
        Assert.Equal("W5CBW", ve.Call);
    }

    [Fact]
    public void NeitherShapePresent_ReturnsEmpty()
    {
        var export = new ExamToolsFullExport();

        Assert.Empty(export.ResolveVes());
    }

    [Fact]
    public void BothPresent_DevdocTakesPriority()
    {
        // Not a real payload shape (a response only ever has one or the other), but pins the
        // documented precedence rather than leaving it to implementation-detail luck.
        var export = new ExamToolsFullExport
        {
            Devdoc = new ExamToolsFullExportDevDoc { Ves = [new ExamToolsVe { Call = "DEVDOC-WINS" }] },
            Ves = [new ExamToolsVe { Call = "TOP-LEVEL" }]
        };

        var result = export.ResolveVes();

        Assert.Equal("DEVDOC-WINS", Assert.Single(result).Call);
    }
}
