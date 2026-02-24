using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cathedral.Utils;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Wingman;

public interface ISettingsService
{
    Task<WingmanSettings> LoadAsync(CancellationToken ct = default);
    Task SaveKeyAsync(string key, CancellationToken ct = default);
    Task<string?> ValidateKeyAsync(string key, CancellationToken ct = default);
    Task<List<string>> GetMemoriesAsync(CancellationToken ct = default);
    Task UpdateMemoriesAsync(Action<List<string>> update, CancellationToken ct = default);
}

public class SettingsService : ISettingsService
{
    // rng: head -c 128 /dev/urandom | base64 -w0
    private static readonly byte[] Entropy = Convert.FromBase64String(
        "ffJAq3AxgqeyBD8XfMM06JFNMdkr28/5yHyYVqD7DdKw9hn7GYzKnlH5subI0YAu2FbuFRHwj4eku1gjHNlmJ7VsWLcxRUTcEZf9BMEDSvCdd0Mk+DyZqv4THHrVKikjW7tH8F5sim2z819nZHtbPy+AtOOJEtVLqt7mzcaLoCM=");

    private readonly IFileSafe<WingmanSettings> _store;

    public SettingsService()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wingman");
        _store = FileSafe.Create<WingmanSettings>(
            path,
            json => Task.FromResult(ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.LocalMachine)),
            bytes => Task.FromResult(Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine))));
    }

    public Task<WingmanSettings> LoadAsync(CancellationToken ct = default) => _store.Read(ct);

    public Task SaveKeyAsync(string key, CancellationToken ct = default) =>
        _store.Update(s => s.OpenAiApiKey = key, ct);

    public async Task<List<string>> GetMemoriesAsync(CancellationToken ct = default)
    {
        var s = await _store.Read(ct);
        return s.Memories ?? [];
    }

    public Task UpdateMemoriesAsync(Action<List<string>> update, CancellationToken ct = default) =>
        _store.Update(s => { s.Memories ??= []; update(s.Memories); }, ct);

    public async Task<string?> ValidateKeyAsync(string key, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var client = new OpenAIClient(key)
                .GetResponsesClient(Constants.GuardModel)
                .AsIChatClient();
            await client.GetResponseAsync("hi", new ChatOptions { MaxOutputTokens = 32 }, cts.Token);
            return null;
        }
        catch (OperationCanceledException)
        {
            return "Validation timed out.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
