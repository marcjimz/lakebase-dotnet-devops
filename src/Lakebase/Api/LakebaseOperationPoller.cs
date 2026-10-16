using System.Text.Json;

namespace Lakebase;

public sealed class LakebaseOperationPoller(LakebaseApiClient api)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    public async Task WaitForOperationAsync(JsonElement operation, CancellationToken ct = default)
    {
        if (!operation.TryGetProperty("name", out var nameElement))
            return;

        var operationName = nameElement.GetString();
        if (string.IsNullOrEmpty(operationName))
            return;

        if (operation.TryGetProperty("done", out var doneElement) && doneElement.GetBoolean())
        {
            EnsureOperationSucceeded(operation);
            return;
        }

        var path = $"/api/2.0/postgres/{operationName}";
        var deadline = DateTime.UtcNow + MaxWait;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);

            using var status = await api.GetAsync(path, ct);
            if (!status.RootElement.TryGetProperty("done", out var done) || !done.GetBoolean())
                continue;

            EnsureOperationSucceeded(status.RootElement);
            return;
        }

        throw new TimeoutException($"Lakebase operation timed out after {MaxWait.TotalMinutes:0} minutes: {operationName}");
    }

    private static void EnsureOperationSucceeded(JsonElement operation)
    {
        if (!operation.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
            throw new InvalidOperationException($"Lakebase operation failed: {message.GetString()}");

        throw new InvalidOperationException($"Lakebase operation failed: {error}");
    }
}
