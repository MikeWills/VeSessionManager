using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// Builds a real <see cref="IServiceScopeFactory"/> over a throwaway SQLite database so a job's
/// <c>RunTickAsync</c> can be driven exactly once (issue #325).
///
/// <para><b>Real SQLite, not EF InMemory</b>, for three reasons that all apply here:
/// <c>ExecuteUpdateAsync</c> — which the ingestion stamp now uses — is unsupported on InMemory
/// entirely; InMemory ignores transactions; and the whole point of these tests is scope and
/// change-tracker behavior, where a provider that fakes persistence proves nothing. Same reasoning
/// as <c>VolunteerExaminerMergeSqliteTests</c>.</para>
///
/// <para><b>One connection, many scopes.</b> `DataSource=:memory:` lives exactly as long as its
/// connection, so the harness holds one open for the fixture's lifetime and hands every scope a
/// context over it. That is what makes "scope per team" observable at all: each scope gets its own
/// <see cref="AppDbContext"/> and change tracker, over one shared database.</para>
/// </summary>
internal sealed class WorkerTickHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public IServiceScopeFactory ScopeFactory { get; }
    public IConfiguration Configuration { get; }
    public RecordingLoggerProvider Logs { get; }

    private WorkerTickHarness(SqliteConnection connection, ServiceProvider provider, RecordingLoggerProvider logs)
    {
        _connection = connection;
        _provider = provider;
        Logs = logs;
        ScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        Configuration = provider.GetRequiredService<IConfiguration>();
    }

    /// <param name="configure">
    /// Registers the services the job under test resolves from its per-tick scope. Deliberately not
    /// a copy of the Worker's own Program.cs: a test should say out loud which collaborators the tick
    /// actually touches, and a drifting copy of the real registration would be worse than none.
    /// </param>
    public static async Task<WorkerTickHarness> CreateAsync(Action<IServiceCollection> configure)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Scoped);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();

        var logs = new RecordingLoggerProvider();
        configure(services);

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        }

        return new WorkerTickHarness(connection, provider, logs);
    }

    /// <summary>A context for arranging and asserting, separate from anything the tick uses.</summary>
    public AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    public async Task<Team> SeedTeamAsync(string name)
    {
        await using var dbContext = NewContext();
        var team = new Team { Name = name, ExamToolsTeamCode = name, CreatedUtc = DateTime.UtcNow };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>Captures log output so "it was handled" can be told apart from "it vanished".</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Recording(this);
    public void Dispose() { }

    private sealed class Recording(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

/// <summary>Matches the fixture used across Core's tests — no new package, per this repo's convention.</summary>
internal sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}

/// <summary>
/// A no-op <see cref="ILogger{T}"/> for the jobs' own constructor parameter, where the test does not
/// care what the job logs.
/// </summary>
internal static class Quiet
{
    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;
}
