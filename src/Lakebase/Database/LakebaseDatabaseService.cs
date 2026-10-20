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
        CliLog.Progress($"==> Auth:      {WorkspaceAuthModes.Resolve(settings)}");
    }

    public async Task<object> GenerateCredentialAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(settings.Endpoint))
            throw new InvalidOperationException("Set LAKEBASE_ENDPOINT, or LAKEBASE_PROJECT_ID + LAKEBASE_BRANCH_ID, in .env");

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
            CliLog.Progress("==> The app and lakebase db commands mint this automatically; use it for psql or other clients.");
        }

        return new
        {
            endpoint = settings.Endpoint,
            token,
            expiresIn,
        };
    }
}
