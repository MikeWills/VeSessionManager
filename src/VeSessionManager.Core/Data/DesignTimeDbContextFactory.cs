using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VeSessionManager.Core.Data;

/// <summary>
/// Lets `dotnet ef migrations add` run against this class library directly, since it has no
/// Program.cs / hosting of its own to build a configured DbContext from. The connection string
/// here only matters for generating migrations — the real one comes from each host project's
/// appsettings at runtime.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite("Data Source=vesessionmanager.db");
        return new AppDbContext(optionsBuilder.Options);
    }
}
