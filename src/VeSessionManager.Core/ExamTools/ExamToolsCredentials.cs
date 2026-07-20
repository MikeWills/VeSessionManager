namespace VeSessionManager.Core.ExamTools;

/// <summary>Per-Team ExamTools login — TeamId keys ExamToolsClient's internal per-team cookie-jar/login cache, distinct from BaseUrl (which stays a global appsettings value, since every team on one deployment hits the same host).</summary>
public sealed record ExamToolsCredentials(int TeamId, string TeamCode, string Username, string Password);
