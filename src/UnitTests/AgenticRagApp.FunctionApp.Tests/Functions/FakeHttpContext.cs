using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace RagApp.UnitTests.Functions;

// Minimal hand-rolled fakes for the isolated-worker HTTP types - there's no official
// lightweight test double for HttpRequestData/HttpResponseData/FunctionContext, and the
// real types only need a handful of members for QueryingFunction.RunQuery to run.
internal sealed class FakeFunctionContext : FunctionContext
{
    public override string InvocationId { get; } = Guid.NewGuid().ToString();
    public override string FunctionId { get; } = "test-function";
    public override TraceContext TraceContext => throw new NotSupportedException();
    public override BindingContext BindingContext => throw new NotSupportedException();
    public override RetryContext RetryContext => throw new NotSupportedException();
    public override IServiceProvider InstanceServices { get; set; } = null!;
    public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();
    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
    public override IInvocationFeatures Features => throw new NotSupportedException();
    public override CancellationToken CancellationToken { get; } = CancellationToken.None;
}

internal sealed class FakeHttpRequestData : HttpRequestData
{
    public FakeHttpRequestData(FunctionContext ctx, string body, string method = "POST") : base(ctx)
    {
        Body    = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Method  = method;
        Url     = new Uri("http://localhost/api/query");
        Headers = new HttpHeadersCollection();
    }

    public override Stream Body { get; }
    public override HttpHeadersCollection Headers { get; }
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = [];
    public override Uri Url { get; }
    public override IEnumerable<ClaimsIdentity> Identities { get; } = [];
    public override string Method { get; }

    public override HttpResponseData CreateResponse() => new FakeHttpResponseData(FunctionContext);
}

internal sealed class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(FunctionContext ctx) : base(ctx)
    {
        Headers = new HttpHeadersCollection();
        Body    = new MemoryStream();
    }

    public override System.Net.HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection      Headers { get; set; }
    public override Stream                     Body { get; set; }

    public string ReadBodyAsString()
    {
        Body.Position = 0;
        using var reader = new StreamReader(Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
