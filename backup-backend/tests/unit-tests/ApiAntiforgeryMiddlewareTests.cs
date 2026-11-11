using BackupPlatform.Api.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.UnitTests;

public sealed class ApiAntiforgeryMiddlewareTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task FrameworkOptOutDoesNotExemptMixedCookieAndTokenEndpoints(string method)
    {
        var antiforgery = new TestAntiforgery { Valid = false };
        var (context, reached) = await InvokeAsync(method, antiforgery, new FrameworkOptOut());
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(reached());
        Assert.Equal(1, antiforgery.Validations);

        antiforgery.Valid = true;
        (context, reached) = await InvokeAsync(method, antiforgery, new FrameworkOptOut());
        Assert.True(reached());
        Assert.Equal(2, antiforgery.Validations);
    }

    [Fact]
    public async Task TokenOnlyAndAgentEndpointsRemainExemptButOrdinaryPostsAreValidated()
    {
        var antiforgery = new TestAntiforgery { Valid = false };
        foreach (var metadata in new object[] { new TokenOnlyEndpoint(), new AgentApiEndpoint() })
        {
            var (_, reached) = await InvokeAsync("POST", antiforgery, metadata);
            Assert.True(reached());
        }
        Assert.Equal(0, antiforgery.Validations);
        var (context, ordinaryReached) = await InvokeAsync("POST", antiforgery);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(ordinaryReached());
        var (_, safeReached) = await InvokeAsync("GET", antiforgery);
        Assert.True(safeReached());
        Assert.Equal(1, antiforgery.Validations);
    }

    private static async Task<(HttpContext Context, Func<bool> Reached)> InvokeAsync(string method, IAntiforgery antiforgery, params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Request.Path = "/api/test";
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        var reached = false;
        await new ApiAntiforgeryMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context, antiforgery);
        return (context, () => reached);
    }

    private sealed class FrameworkOptOut : IAntiforgeryMetadata
    {
        public bool RequiresValidation => false;
    }

    private sealed class TestAntiforgery : IAntiforgery
    {
        public bool Valid { get; set; }
        public int Validations { get; private set; }
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();
        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            Validations++;
            if (!Valid) throw new AntiforgeryValidationException("Invalid token");
            return Task.CompletedTask;
        }
    }
}
