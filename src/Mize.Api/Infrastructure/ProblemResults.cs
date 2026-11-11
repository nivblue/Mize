using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace Mize.Api.Infrastructure;

/// <summary>Builds RFC 7807 problem responses by hand so they work the same in unit tests and at runtime.</summary>
public static class ProblemResults
{
    /// <summary>Creates an HTTP 400 response listing validation errors per field.</summary>
    /// <param name="errors">Errors keyed by field name.</param>
    /// <returns>A 400 result with <see cref="ValidationProblemDetails"/>.</returns>
    public static BadRequestObjectResult ValidationFailed(IDictionary<string, string[]> errors) =>
        new(new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred."
        });

    /// <summary>Creates an HTTP 429 response explaining when the supplier may retry.</summary>
    /// <param name="retryAfter">Time until the rate-limit window frees a slot.</param>
    /// <returns>A 429 result with <see cref="ProblemDetails"/>.</returns>
    public static ObjectResult TooManyRequests(TimeSpan retryAfter) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests.",
            Detail = $"Rate limit exceeded. Retry in {RetryAfterSeconds(retryAfter).ToString(CultureInfo.InvariantCulture)} second(s)."
        })
        {
            StatusCode = StatusCodes.Status429TooManyRequests
        };

    /// <summary>Converts a delay to whole seconds for the <c>Retry-After</c> header, rounding up, minimum 1.</summary>
    /// <param name="retryAfter">The delay.</param>
    /// <returns>Whole seconds (at least 1).</returns>
    public static int RetryAfterSeconds(TimeSpan retryAfter) =>
        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
}
