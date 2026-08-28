using System.ComponentModel.DataAnnotations;

namespace HookRelay.Api;

/// <summary>
/// Reads the tenant the caller is acting for, and validates request bodies.
/// </summary>
/// <remarks>
/// The tenant arrives in a header because authentication belongs to the platform this service sits inside,
/// not to this service. Everything downstream still filters on it, so swapping the header for a claim on a
/// verified token is a one-method change rather than an audit of every query.
/// </remarks>
public static class RequestContext
{
    /// <summary>Header carrying the tenant id.</summary>
    public const string TenantHeader = "X-Tenant-Id";

    /// <summary>Reads and parses the tenant header.</summary>
    /// <param name="request">Incoming request.</param>
    /// <param name="tenantId">The parsed tenant id.</param>
    public static bool TryGetTenantId(HttpRequest request, out Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(request);

        tenantId = Guid.Empty;
        return request.Headers.TryGetValue(TenantHeader, out Microsoft.Extensions.Primitives.StringValues values)
            && Guid.TryParse(values.ToString(), out tenantId)
            && tenantId != Guid.Empty;
    }

    /// <summary>The problem response returned when the tenant header is missing or malformed.</summary>
    public static IResult MissingTenant() => Results.Problem(
        detail: $"A valid {TenantHeader} header is required.",
        statusCode: StatusCodes.Status400BadRequest,
        title: "Missing tenant");

    /// <summary>Validates a request body against its data annotations.</summary>
    /// <param name="model">The body.</param>
    /// <param name="problem">A validation problem response, when the body is invalid.</param>
    /// <typeparam name="TModel">Body type.</typeparam>
    public static bool TryValidate<TModel>(TModel model, out IResult problem)
        where TModel : notnull
    {
        List<ValidationResult> results = [];
        if (Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true))
        {
            problem = Results.Empty;
            return true;
        }

        Dictionary<string, string[]> errors = results
            .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty),
                (result, member) => (Member: member, result.ErrorMessage))
            .GroupBy(entry => entry.Member, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.ErrorMessage ?? "Invalid value.").ToArray(),
                StringComparer.Ordinal);

        problem = Results.ValidationProblem(errors);
        return false;
    }
}
