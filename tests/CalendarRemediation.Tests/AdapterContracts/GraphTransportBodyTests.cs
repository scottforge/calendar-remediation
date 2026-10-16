using System.Net;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Tests.AdapterContracts;

public sealed class GraphTransportBodyTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(299)]
    [InlineData(412)]
    public async Task PatchPreservesDefinitiveStatusWithoutAccessingResponseContent(int status)
    {
        using var content = new UnreadableContent();
        using var handler = new ResponseHandler(status, content);
        using var client = new HttpClient(handler);
        var transport = new GraphTransport(client, _ => Task.FromResult("synthetic-token"));

        var response = await transport.SendAsync(HttpMethod.Patch, GraphApi.EventPatch("event"),
            "{}", "\"v1\"");

        Assert.Equal(status, response.Status);
        Assert.Equal(status, response.Exchange.Status);
        Assert.Null(response.Body);
        Assert.Equal(0, response.Exchange.Bytes);
        Assert.Equal(0, content.ReadAttempts);
        Assert.Equal(1, handler.SendCount);
    }

    private sealed class ResponseHandler(int status, HttpContent content) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            Assert.Equal(HttpMethod.Patch, request.Method);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        public int ReadAttempts { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempts++;
            throw new IOException("Response content must not be read after a PATCH.");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            ReadAttempts++;
            throw new IOException("Response content must not be opened after a PATCH.");
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => CreateContentReadStreamAsync();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
