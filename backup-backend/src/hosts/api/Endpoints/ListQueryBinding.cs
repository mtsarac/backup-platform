using BackupPlatform.Application.Lists;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// Binds the shared list query string once: take <c>[AsParameters] ListParams list</c>, call <c>list.ToQuery()</c> and pass
/// the <see cref="ListQuery"/> to the store; chain <see cref="ListEndpointExtensions.WithListQuery{TBuilder}"/> on the route
/// so a bad <c>page</c>, <c>pageSize</c>, <c>sort</c> or <c>dir</c> answers 400 <c>invalid_request</c> with that <c>field</c>.
/// </summary>
public sealed class ListParams
{
    public string? Page { get; init; }
    public int? PageSize { get; init; }
    public string? Search { get; init; }
    public string? Sort { get; init; }
    public string? Dir { get; init; }

    public ListQuery ToQuery() => ListQuery.Parse(ListQuery.ParsePage(Page), PageSize, Search, Sort, Dir);
}

public static class ListEndpointExtensions
{
    public static TBuilder WithListQuery<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, ListQueryErrorFilter>();

    private sealed class ListQueryErrorFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (InvalidListQueryException exception)
            {
                return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = "invalid_value" });
            }
        }
    }
}
