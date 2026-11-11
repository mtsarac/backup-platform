using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BackupPlatform.Api.Security;
using BackupPlatform.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BackupPlatform.IntegrationTests;

/// <summary>Hostile input at the API edge: refused with a controlled status, never a 500, never a database or exception detail in the body.</summary>
public sealed class ApiBoundaryIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Nul = "a\u0000b";
    private static readonly string[] Leaks = ["npgsql", "postgres", "sqlstate", "22021", "exception", "stack", "   at ", "invalid byte sequence", "identity must not be empty"];

    private static async Task AssertNoLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        foreach (var leak in Leaks) Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Route(RouteEndpoint Endpoint, string Method)
    {
        public string Pattern => Endpoint.RoutePattern.RawText!;
        public MethodInfo? Handler => Endpoint.Metadata.GetMetadata<MethodInfo>();

        /// <summary>The pattern with <paramref name="value"/> in <paramref name="target"/> and a plausible value everywhere else; null target fills them all.</summary>
        public string Fill(Guid tenantId, string? target, string value) => Regex.Replace(Pattern, @"\{\*?(\w+)(?::(\w+))?\??\}", match =>
        {
            var name = match.Groups[1].Value;
            if (name == target) return Uri.EscapeDataString(value);
            if (name == "tenantId") return tenantId.ToString();
            return match.Groups[2].Value == "int" ? "1" : Guid.NewGuid().ToString();
        });

        public IEnumerable<string> GuidParameters => Endpoint.RoutePattern.Parameters.Select(p => p.Name).Where(name => IsGuid(name));

        public IEnumerable<ParameterInfo> QueryParameters(Func<Type, bool> typeFilter) => (Handler?.GetParameters() ?? [])
            .Where(p => p.Name is not null && Endpoint.RoutePattern.GetParameter(p.Name) is null && typeFilter(p.ParameterType)
                && p.GetCustomAttributes().All(a => a is not (FromHeaderAttribute or FromServicesAttribute or FromBodyAttribute)));

        public Type? BodyType => Endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is { ContentTypes: var types, RequestType: { } type } && types.Contains("application/json") ? type : null;

        private bool IsGuid(string name) =>
            Endpoint.RoutePattern.GetParameter(name)!.ParameterPolicies.Any(policy => policy.Content == "guid")
            || Handler?.GetParameters().Any(p => p.Name == name && Nullable.GetUnderlyingType(p.ParameterType) is var t && (t ?? p.ParameterType) == typeof(Guid)) == true;
    }

    private static List<Route> Routes(IServiceProvider services) => services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
        .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(method => new Route(endpoint, method)))
        .OrderBy(route => route.Pattern, StringComparer.Ordinal).ThenBy(route => route.Method, StringComparer.Ordinal).ToList();

    private sealed record Actors(AgentWorld World, TenantSetup Tenant, BrowserSession Platform, EnrolledAgent Agent)
    {
        /// <summary>The in-memory host rethrows an unhandled exception where Kestrel would answer 500; report it as that 500.</summary>
        public async Task<HttpResponseMessage> SendAsync(Route route, string path, object? body = null)
        {
            var method = new HttpMethod(route.Method);
            try
            {
                if (route.Pattern.StartsWith("/api/agent/", StringComparison.Ordinal))
                    return await World.SendAgentAsync(method, path, Agent.CredentialId, Agent.Secret, "1.0", body);
                return await (route.Pattern.StartsWith("/api/platform/", StringComparison.Ordinal) ? Platform : Tenant.Admin).SendAsync(method, path, body);
            }
            catch (Exception exception)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = $"{exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ")}", Content = new StringContent(string.Empty) };
            }
        }
    }

    private async Task<(Actors Actors, AgentWorld World)> StartAsync()
    {
        var world = await AgentWorld.CreateAsync(fixture);
        var tenant = await world.CreateTenantAsync(TenantRole.Owner);
        var platform = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        return (new Actors(world, tenant, platform, await world.EnrollAsync(tenant)), world);
    }

    private static object? EmptyBodyFor(Route route) => route.Method is "GET" or "DELETE" ? null : new { };

    [Fact]
    public async Task AntiforgeryExemptionsAreLimitedToTokenOnlyRecipientAndAgentRoutes()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var routes = Routes(world.Factory.Services);
        var recipients = routes.Where(route => route.Endpoint.Metadata.GetMetadata<TokenOnlyEndpoint>() is not null).ToArray();
        var recipient = Assert.Single(recipients);
        Assert.Equal(("/api/notifications/recipient/{route}", "POST"), (recipient.Pattern, recipient.Method));
        Assert.True(AnonymousAllowed(recipient));

        var agents = routes.Where(route => route.Endpoint.Metadata.GetMetadata<AgentApiEndpoint>() is not null).ToArray();
        Assert.NotEmpty(agents);
        foreach (var route in agents)
        {
            if (route.Pattern == "/api/agent/enroll")
            {
                Assert.Equal("POST", route.Method);
                Assert.True(AnonymousAllowed(route));
            }
            else
            {
                Assert.False(AnonymousAllowed(route));
                var authorization = Assert.Single(route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                Assert.Equal(AuthorizationPolicies.Agent, authorization.Policy);
                Assert.True(string.IsNullOrEmpty(authorization.AuthenticationSchemes));
            }
        }
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("\u0000")]
    [InlineData("admin\u0000")]
    [InlineData("a\u0001b")]
    [InlineData("a\nb")]
    [InlineData("\ud800")]
    public async Task Login_with_a_control_character_in_the_identifier_is_refused_without_a_database_error(string identifier)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var browser = new BrowserSession(world.Factory.CreateBrowser());

        using var response = await browser.SendAsync(HttpMethod.Post, "/api/auth/login", new { loginIdentifier = identifier, password = "Some-password-1!" });

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, $"{(int)response.StatusCode}");
        await AssertNoLeakAsync(response);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task A_NUL_in_a_JSON_text_field_is_a_400_on_a_real_endpoint_and_nothing_is_stored()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platform = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var marker = $"nul-{Guid.NewGuid():N}";

        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/platform/tenants", new { name = marker + Nul }),
            ("/api/platform/users", new { loginIdentifier = marker + Nul, displayName = "Name", password = "Local-only-password-1!" }),
            ("/api/platform/users", new { loginIdentifier = marker, displayName = marker + Nul, password = "Local-only-password-1!" }),
        })
        {
            using var response = await platform.SendAsync(HttpMethod.Post, path, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNoLeakAsync(response);
        }
        await using var db = fixture.CreatePlatformContext();
        Assert.False(db.Tenants.Any(tenant => tenant.Name.StartsWith(marker)));
        Assert.False(db.Users.Any(user => user.DisplayName.StartsWith(marker)));
    }

    [Theory]
    [InlineData("/api/platform/users?search=a%00b")]
    [InlineData("/api/platform/agents?search=a%00b")]
    [InlineData("/api/platform/plans?search=a%00b")]
    [InlineData("/api/platform/storage-usage?search=a%00b")]
    [InlineData("/api/platform/storage-connections?search=a%00b")]
    public async Task A_NUL_in_a_search_filter_is_a_400(string path)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platform = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));

        using var response = await platform.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoLeakAsync(response);
    }

    [Fact]
    public async Task Every_JSON_request_type_rejects_a_NUL_in_any_string_and_every_route_answers_400()
    {
        var (actors, world) = await StartAsync();
        await using var _ = world;
        var options = world.Factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var failures = new List<string>();
        var covered = 0;

        foreach (var route in Routes(world.Factory.Services))
        {
            if (route.BodyType is not { } type || Poisoned(type) is not JsonObject json) continue;
            // Agent routes validate their own payload (and name the field); for them a NUL only has to be a controlled answer, never a 500.
            var agentRoute = route.Pattern.StartsWith("/api/agent/", StringComparison.Ordinal);
            if (!agentRoute)
            {
                covered++;
                if (!Throws(type, json, options)) failures.Add($"{route.Method} {route.Pattern}: {type.Name} accepted a NUL");
            }
            using var response = await actors.SendAsync(route, route.Fill(actors.Tenant.Tenant.Id.Value, null, string.Empty), json);
            if (agentRoute ? response.StatusCode == HttpStatusCode.InternalServerError : response.StatusCode != HttpStatusCode.BadRequest) failures.Add($"{route.Method} {route.Pattern}: {(int)response.StatusCode} {response.ReasonPhrase}");
            await AssertNoLeakAsync(response);
        }

        Assert.True(covered > 40, $"only {covered} JSON routes were covered");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("/api/agent-dist/packages/a%00b")]
    [InlineData("/api/agent/updates/packages/linux/a%00b")]
    [InlineData("/api/notifications/recipient/a%00b")]
    public async Task A_NUL_in_a_text_route_value_is_a_400_before_any_handler_runs(string path)
    {
        // The in-memory host throws on such a path; only a real listener shows what a client gets.
        await using var world = await AgentWorld.CreateAsync(fixture, kestrel: true);

        using var response = await world.Anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoLeakAsync(response);
    }

    [Fact]
    public async Task An_all_zero_id_inside_a_JSON_body_is_never_a_server_error()
    {
        var (actors, world) = await StartAsync();
        await using var _ = world;
        var failures = new List<string>();
        var covered = 0;

        foreach (var route in Routes(world.Factory.Services))
        {
            if (route.BodyType is not { } type || !HasGuid(type) || WithZeroIds(type) is not JsonObject json) continue;
            covered++;
            using var response = await actors.SendAsync(route, route.Fill(actors.Tenant.Tenant.Id.Value, null, string.Empty), json);
            if ((int)response.StatusCode >= 500) failures.Add($"{route.Method} {route.Pattern}: {(int)response.StatusCode} {response.ReasonPhrase}");
            await AssertNoLeakAsync(response);
        }

        Assert.True(covered > 5, $"only {covered} JSON routes with an id were covered");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task A_NUL_or_an_all_zero_id_in_a_query_string_is_a_400_on_every_route_that_reads_one()
    {
        var (actors, world) = await StartAsync();
        await using var _ = world;
        var failures = new List<string>();
        var covered = 0;

        foreach (var route in Routes(world.Factory.Services))
        {
            foreach (var parameter in route.QueryParameters(type => (Nullable.GetUnderlyingType(type) ?? type) is var t && (t == typeof(string) || t == typeof(Guid))))
            {
                covered++;
                var value = (Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType) == typeof(Guid) ? Guid.Empty.ToString() : Nul;
                var path = $"{route.Fill(actors.Tenant.Tenant.Id.Value, null, string.Empty)}?{parameter.Name}={Uri.EscapeDataString(value)}";
                using var response = await actors.SendAsync(route, path, EmptyBodyFor(route));
                if (response.StatusCode != HttpStatusCode.BadRequest) failures.Add($"{route.Method} {route.Pattern}?{parameter.Name}: {(int)response.StatusCode} {response.ReasonPhrase}");
                await AssertNoLeakAsync(response);
            }
        }

        Assert.True(covered > 10, $"only {covered} query parameters were covered");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task An_all_zero_id_in_any_route_is_a_400_and_never_a_500()
    {
        var (actors, world) = await StartAsync();
        await using var _ = world;
        var failures = new List<string>();
        var covered = 0;

        foreach (var route in Routes(world.Factory.Services))
        {
            foreach (var parameter in route.GuidParameters)
            {
                covered++;
                var path = route.Fill(actors.Tenant.Tenant.Id.Value, parameter, Guid.Empty.ToString());
                using var response = await actors.SendAsync(route, path, EmptyBodyFor(route));
                if (response.StatusCode != HttpStatusCode.BadRequest) failures.Add($"{route.Method} {route.Pattern} [{parameter}]: {(int)response.StatusCode} {response.ReasonPhrase}");
                await AssertNoLeakAsync(response);
            }
        }

        Assert.True(covered > 150, $"only {covered} guid parameters were covered");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task An_all_zero_id_never_answers_before_authentication_and_authorization()
    {
        var (actors, world) = await StartAsync();
        await using var _ = world;
        var viewer = await world.CreateTenantAsync(TenantRole.Viewer);
        var failures = new List<string>();

        foreach (var route in Routes(world.Factory.Services))
        {
            foreach (var parameter in route.GuidParameters)
            {
                var path = route.Fill(actors.Tenant.Tenant.Id.Value, parameter, Guid.Empty.ToString());
                using var anonymous = await world.Anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(route.Method), path));
                if (anonymous.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && !AnonymousAllowed(route))
                    failures.Add($"anonymous {route.Method} {route.Pattern} [{parameter}]: {(int)anonymous.StatusCode}");
                if (route.Pattern.StartsWith("/api/platform/", StringComparison.Ordinal))
                {
                    using var tenantUser = await viewer.Admin.SendAsync(new HttpMethod(route.Method), path, EmptyBodyFor(route));
                    if (tenantUser.StatusCode != HttpStatusCode.Forbidden) failures.Add($"tenant user {route.Method} {route.Pattern} [{parameter}]: {(int)tenantUser.StatusCode}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Routes that deliberately serve anyone: the email recipient links carry their own token.
    private static bool AnonymousAllowed(Route route) => route.Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;

    private static bool Throws(Type type, JsonObject json, JsonSerializerOptions options)
    {
        try { JsonSerializer.Deserialize(json.ToJsonString(), type, options); return false; }
        catch (JsonException) { return true; }
    }

    /// <summary>A JSON value for <paramref name="type"/> built from <paramref name="leaf"/> values (null leaves are left out); null when no leaf is reachable.</summary>
    private static JsonNode? Shape(Type type, Func<Type, JsonNode?> leaf, string key, int depth = 0)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (leaf(type) is { } value) return value;
        if (depth > 4 || type.IsPrimitive || type.IsEnum || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !type.IsGenericType && !type.IsArray) return null;
        var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() is var d && (d == typeof(IDictionary<,>) || d == typeof(IReadOnlyDictionary<,>)));
        if (dictionary is not null && dictionary.GetGenericArguments() is [var keyType, var valueType] && keyType == typeof(string))
            return Shape(valueType, leaf, key, depth + 1) is { } item ? new JsonObject { [key] = item } : null;
        if (type.IsArray || type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            return Shape(type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0], leaf, key, depth + 1) is { } element ? new JsonArray(element) : null;
        var result = new JsonObject();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetMethod is not null))
            if (Shape(property.PropertyType, leaf, key, depth + 1) is { } child) result[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = child;
        return result.Count == 0 ? null : result;
    }

    private static JsonNode? Poisoned(Type type) => Shape(type, leaf: t => t == typeof(string) ? JsonValue.Create(Nul) : null, key: Nul);

    private static JsonNode? WithZeroIds(Type type) => Shape(type, leaf: t => t == typeof(Guid) ? JsonValue.Create(Guid.Empty) : t == typeof(string) ? JsonValue.Create("x") : null, key: "k");

    private static bool HasGuid(Type type) => Shape(type, leaf: t => t == typeof(Guid) ? JsonValue.Create(Guid.Empty) : null, key: "k") is not null;
}
