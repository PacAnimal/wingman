using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wingman.Tests;

[TestFixture]
public class SessionTrackerTests
{
    // fake client returning a fixed response text
    private sealed class FakeChatClient(string responseText) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // fake client returning responses from a queue
    private sealed class QueuedChatClient(Queue<string> responses) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = responses.Count > 0 ? responses.Dequeue() : "{}";
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // fake client that always throws
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated failure");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static CommandResult MakeResult(string output = "", int exitCode = 0) =>
        new("Connect-AzAccount", output, exitCode, exitCode == 0, "C:\\", false, "00:00:01");

    private static UserCommandInfo MakeUserCmd(string cmd, string output = "", bool success = true) =>
        new(cmd, output, success ? 0 : 1, success, "C:\\");

    // -------------------------------------------------------------------------

    [TestFixture]
    public class ProcessAuthCommandAsyncTests
    {
        [Test]
        public async Task Login_RecordsSession()
        {
            var client = new FakeChatClient("""{"type":"login","service":"azure","identity":"user@contoso.com"}""");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);

            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult("Logged in"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.Sessions, Has.Count.EqualTo(1));
                Assert.That(tracker.Sessions["azure"].Identity, Is.EqualTo("user@contoso.com"));
                Assert.That(tracker.Sessions["azure"].Service, Is.EqualTo("azure"));
            }
        }

        [Test]
        public async Task Logout_RemovesSession()
        {
            var responses = new Queue<string>([
                """{"type":"login","service":"azure","identity":"user@contoso.com"}""",
                """{"type":"logout","service":"azure","identity":""}""",
            ]);
            var tracker = new SessionTracker(new QueuedChatClient(responses), NullLogger<SessionTracker>.Instance);

            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult());
            Assert.That(tracker.Sessions, Has.Count.EqualTo(1));

            await tracker.ProcessAuthCommandAsync("Disconnect-AzAccount", MakeResult());
            Assert.That(tracker.Sessions, Is.Empty);
        }

        [Test]
        public async Task FailedLogin_NotRecorded()
        {
            var client = new FakeChatClient("""{"type":"failed","service":"azure","identity":""}""");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);

            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult(exitCode: 1));

            Assert.That(tracker.Sessions, Is.Empty);
        }

        [Test]
        public async Task SecondLogin_SameService_Overwrites()
        {
            var responses = new Queue<string>([
                """{"type":"login","service":"azure","identity":"user1@contoso.com"}""",
                """{"type":"login","service":"azure","identity":"user2@contoso.com"}""",
            ]);
            var tracker = new SessionTracker(new QueuedChatClient(responses), NullLogger<SessionTracker>.Instance);

            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult());
            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.Sessions, Has.Count.EqualTo(1));
                Assert.That(tracker.Sessions["azure"].Identity, Is.EqualTo("user2@contoso.com"));
            }
        }

        [Test]
        public void GuardThrows_NoCrash_NoSession()
        {
            var tracker = new SessionTracker(new ThrowingChatClient(), NullLogger<SessionTracker>.Instance);

            Assert.DoesNotThrowAsync(() => tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult()));
            Assert.That(tracker.Sessions, Is.Empty);
        }
    }

    [TestFixture]
    public class FormatForContextTests
    {
        [Test]
        public void Empty_ReturnsNull()
        {
            var tracker = new SessionTracker(new FakeChatClient("{}"), NullLogger<SessionTracker>.Instance);
            Assert.That(tracker.FormatForContext(), Is.Null);
        }

        [Test]
        public async Task WithSession_ContainsServiceAndIdentity()
        {
            var client = new FakeChatClient("""{"type":"login","service":"azure","identity":"user@contoso.com"}""");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);
            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult());

            var context = tracker.FormatForContext();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context, Is.Not.Null);
                Assert.That(context, Does.Contain("azure"));
                Assert.That(context, Does.Contain("user@contoso.com"));
                Assert.That(context, Does.StartWith("[active sessions"));
            }
        }
    }

    [TestFixture]
    public class ClearTests
    {
        [Test]
        public async Task Clear_RemovesAllSessions()
        {
            var responses = new Queue<string>([
                """{"type":"login","service":"azure","identity":"a@b.com"}""",
                """{"type":"login","service":"aws","identity":"user"}""",
            ]);
            var tracker = new SessionTracker(new QueuedChatClient(responses), NullLogger<SessionTracker>.Instance);
            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult());
            await tracker.ProcessAuthCommandAsync("aws configure", MakeResult());
            Assert.That(tracker.Sessions, Has.Count.EqualTo(2));

            tracker.Clear();
            Assert.That(tracker.Sessions, Is.Empty);
        }

        [Test]
        public async Task Clear_FormatForContext_ReturnsNullAfterClear()
        {
            var client = new FakeChatClient("""{"type":"login","service":"azure","identity":"a@b.com"}""");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);
            await tracker.ProcessAuthCommandAsync("Connect-AzAccount", MakeResult());
            Assert.That(tracker.FormatForContext(), Is.Not.Null);

            tracker.Clear();
            Assert.That(tracker.FormatForContext(), Is.Null);
        }
    }

    [TestFixture]
    public class ProcessUserCommandsAsyncTests
    {
        [Test]
        public async Task EmptyList_MakesNoCall()
        {
            var client = new FakeChatClient("[]");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);

            await tracker.ProcessUserCommandsAsync([]);

            Assert.That(client.CallCount, Is.Zero);
        }

        [Test]
        public async Task MultipleCmds_OneSingleCall()
        {
            var client = new FakeChatClient("[]");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);

            await tracker.ProcessUserCommandsAsync([MakeUserCmd("cmd1"), MakeUserCmd("cmd2"), MakeUserCmd("cmd3")]);

            Assert.That(client.CallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task BatchWithLogin_RecordsSession()
        {
            var client = new FakeChatClient("""[{"index":1,"type":"login","service":"azure","identity":"user@contoso.com"}]""");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);

            await tracker.ProcessUserCommandsAsync([MakeUserCmd("Connect-AzAccount", "Logged in")]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.Sessions, Has.Count.EqualTo(1));
                Assert.That(tracker.Sessions["azure"].Identity, Is.EqualTo("user@contoso.com"));
            }
        }

        [Test]
        public async Task BatchWithLogout_RemovesSession()
        {
            var responses = new Queue<string>([
                """[{"index":1,"type":"login","service":"azure","identity":"user@contoso.com"}]""",
                """[{"index":1,"type":"logout","service":"azure","identity":""}]""",
            ]);
            var tracker = new SessionTracker(new QueuedChatClient(responses), NullLogger<SessionTracker>.Instance);

            await tracker.ProcessUserCommandsAsync([MakeUserCmd("Connect-AzAccount")]);
            Assert.That(tracker.Sessions, Has.Count.EqualTo(1));

            await tracker.ProcessUserCommandsAsync([MakeUserCmd("Disconnect-AzAccount")]);
            Assert.That(tracker.Sessions, Is.Empty);
        }

        [Test]
        public async Task BatchEmptyResult_NoSessionsAdded()
        {
            var client = new FakeChatClient("[]");
            var tracker = new SessionTracker(client, NullLogger<SessionTracker>.Instance);

            await tracker.ProcessUserCommandsAsync([MakeUserCmd("Get-Process"), MakeUserCmd("ls")]);

            Assert.That(tracker.Sessions, Is.Empty);
        }

        [Test]
        public void BatchThrows_NoCrash()
        {
            var tracker = new SessionTracker(new ThrowingChatClient(), NullLogger<SessionTracker>.Instance);

            Assert.DoesNotThrowAsync(() =>
                tracker.ProcessUserCommandsAsync([MakeUserCmd("Connect-AzAccount")]));
        }
    }
}
