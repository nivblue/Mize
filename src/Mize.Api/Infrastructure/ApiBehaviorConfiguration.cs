using Microsoft.AspNetCore.Mvc;

namespace Mize.Api.Infrastructure;

/// <summary>
/// Customises MVC's automatic 400 response for payloads that cannot be bound (malformed JSON, missing
/// required properties, wrong types) so these are logged at Warning like every other validation failure.
/// </summary>
public static class ApiBehaviorConfiguration
{
    /// <summary>
    /// Logs the binding errors at Warning and returns the standard validation problem response.
    /// Plugged into <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c>.
    /// </summary>
    /// <param name="context">The action context holding the invalid model state.</param>
    /// <returns>An HTTP 400 result.</returns>
    public static IActionResult CreateInvalidModelStateResponse(ActionContext context)
    {
        var errors = context.ModelState
            .Where(entry => entry.Value!.Errors.Count > 0)   // entries of a ModelStateDictionary are never null
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value!.Errors
                    .Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "The value is invalid." : e.ErrorMessage)
                    .ToArray());

        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Mize.Api.ModelBinding");
        logger.LogWarning(
            "Rejected request to {Path}: payload could not be bound ({ErrorCount} error(s)): {Errors}",
            context.HttpContext.Request.Path.Value, errors.Count, string.Join("; ", errors.Keys));

        return ProblemResults.ValidationFailed(errors);
    }
}
