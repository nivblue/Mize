using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mize.Api.Infrastructure;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="ProblemResults"/> and <see cref="ApiBehaviorConfiguration"/>.</summary>
public class InfrastructureTests
{
    /// <summary>Retry-After is rounded up to whole seconds and is never below 1.</summary>
    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.2, 1)]
    [InlineData(12.2, 13)]
    [InlineData(60.0, 60)]
    public void RetryAfterSeconds_RoundsUp_WithMinimumOfOne(double seconds, int expected)
    {
        Assert.Equal(expected, ProblemResults.RetryAfterSeconds(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>The 429 result carries status 429 and a problem body that mentions the delay.</summary>
    [Fact]
    public void TooManyRequests_Has429AndDetail()
    {
        var result = ProblemResults.TooManyRequests(TimeSpan.FromSeconds(30));

        Assert.Equal(429, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(429, problem.Status);
        Assert.Contains("30", problem.Detail);
    }

    /// <summary>The 400 result carries the supplied field errors.</summary>
    [Fact]
    public void ValidationFailed_Has400AndErrors()
    {
        var result = ProblemResults.ValidationFailed(new Dictionary<string, string[]> { ["price"] = new[] { "bad" } });

        Assert.Equal(400, result.StatusCode);
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Equal(new[] { "bad" }, details.Errors["price"]);
    }

    /// <summary>Binding errors become a 400 with the field errors and are logged at Warning.</summary>
    [Fact]
    public void CreateInvalidModelStateResponse_Returns400_AndLogsWarning()
    {
        var provider = new ListLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(provider))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Path = "/api/reservations/ingest";
        var context = new ActionContext(http, new RouteData(), new ActionDescriptor());
        context.ModelState.AddModelError("roomId", "The roomId field is required.");
        context.ModelState.AddModelError("", "");

        var result = ApiBehaviorConfiguration.CreateInvalidModelStateResponse(context);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var details = Assert.IsType<ValidationProblemDetails>(bad.Value);
        Assert.Equal("The roomId field is required.", Assert.Single(details.Errors["roomId"]));
        Assert.Equal("The value is invalid.", Assert.Single(details.Errors[""]));
        Assert.Equal(LogLevel.Warning, Assert.Single(provider.Entries).Level);
    }
}
