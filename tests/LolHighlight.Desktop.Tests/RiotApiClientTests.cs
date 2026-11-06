using System.Net;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class RiotApiClientTests
{
    [Fact]
    public async Task CallerCancellation_RemainsCancellationInsteadOfMatchUnavailable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new RiotApiClient(new DelegateHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancellation = new CancellationTokenSource();
        var request = client.GetGameStatsAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Events_NormalizeNestedJsonAndUseIncrementalCursor()
    {
        using var client = new RiotApiClient(new DelegateHandler((request, _) =>
        {
            Assert.Equal("https://127.0.0.1:2999/liveclientdata/eventdata?eventID=10", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Events\":[{\"EventID\":11,\"EventName\":\"Ace\",\"AcingTeam\":\"ORDER\"}]}")
            });
        }));
        var events = await client.GetEventsAsync(10);
        Assert.Equal(11L, events.Single()["EventID"]);
        Assert.Equal("Ace", events.Single()["EventName"]);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
