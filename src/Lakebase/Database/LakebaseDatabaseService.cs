using System.Text.Json;

namespace Lakebase;

public sealed class LakebaseDatabaseService(
    DatabaseConnectionSettings settings,
    LakebaseApiClient api)
{
    public void PrintTarget()
    {
        CliLog.Progress($"==> Workspace: {settings.DatabricksHost}");
        if (!string.IsNullOrEmpty(settings.Endpoint))
            CliLog.Progress($"==> Endpoint:  {settings.Endpoint}");
        if (!string.IsNullOrEmpty(settings.ClientId) && !string.IsNullOrEmpty(settings.ClientSecret))
            CliLog.Progress("==> Auth:      database service principal / app SP");
        else if (!string.IsNullOrEmpty(settings.DatabricksToken))
            CliLog.Progress("==> Auth:      database token");
    }

    public async Task<object> GenerateCredentialAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(settings.Endpoint))
            throw new InvalidOperationException("Set LAKEBASE_ENDPOINT in .env.database");

        PrintTarget();
        CliLog.Progress("==> Generating Lakebase database credential...");

        using var result = await api.PostAsync(
            "/api/2.0/postgres/credentials",
            new { endpoint = settings.Endpoint },
            ct);

        var token = result.RootElement.TryGetProperty("token", out var tokenEl)
            ? tokenEl.GetString()
            : null;
        var expiresIn = result.RootElement.TryGetProperty("expires_in", out var expEl)
            && expEl.TryGetInt32(out var seconds)
            ? seconds
            : (int?)null;

        if (!CliLog.JsonMode)
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            CliLog.Dump(json);
            CliLog.Progress("==> Token expires in ~1 hour.");
            CliLog.Progress("==> Copy token into DATABASE_URL Password= in .env.database");
        }

        return new
        {
            endpoint = settings.Endpoint,
            token,
            expiresIn,
        };
    }
}
