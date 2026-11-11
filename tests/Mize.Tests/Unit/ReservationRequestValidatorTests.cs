using Mize.Api.Validation;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="ReservationRequestValidator"/>.</summary>
public class ReservationRequestValidatorTests
{
    private readonly ReservationRequestValidator _validator = new();

    /// <summary>A fully valid request produces no errors.</summary>
    [Fact]
    public void Validate_ValidRequest_ReturnsNoErrors()
    {
        Assert.Empty(_validator.Validate(TestData.Request()));
    }

    /// <summary>Empty or whitespace ids are rejected, with the error keyed by the field name.</summary>
    [Theory]
    [InlineData("", "res-1", "room-1", "supplierId")]
    [InlineData("  ", "res-1", "room-1", "supplierId")]
    [InlineData("sup", "", "room-1", "reservationId")]
    [InlineData("sup", "res-1", " ", "roomId")]
    public void Validate_BlankText_ReturnsErrorForThatField(string supplier, string reservation, string room, string expectedKey)
    {
        var errors = _validator.Validate(TestData.Request(supplier, reservation, room));

        Assert.Single(errors);
        Assert.True(errors.ContainsKey(expectedKey));
    }

    /// <summary>checkOut equal to or before checkIn is rejected.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_CheckOutNotAfterCheckIn_ReturnsCheckOutError(int daysAfterCheckIn)
    {
        var request = TestData.Request(checkOut: TestData.CheckIn.AddDays(daysAfterCheckIn));

        var errors = _validator.Validate(request);

        Assert.True(errors.ContainsKey("checkOut"));
    }

    /// <summary>A negative price is rejected; zero is allowed.</summary>
    [Fact]
    public void Validate_NegativePrice_ReturnsPriceError_ButZeroIsAllowed()
    {
        Assert.True(_validator.Validate(TestData.Request(price: -0.01m)).ContainsKey("price"));
        Assert.Empty(_validator.Validate(TestData.Request(price: 0m)));
    }

    /// <summary>A timestamp with a non-zero offset is rejected for each of the three date fields.</summary>
    [Fact]
    public void Validate_NonUtcTimestamps_ReturnsErrorPerField()
    {
        var plusTwo = TimeSpan.FromHours(2);
        var request = TestData.Request(
            checkIn: new DateTimeOffset(2026, 8, 1, 2, 0, 0, plusTwo),
            checkOut: new DateTimeOffset(2026, 8, 3, 2, 0, 0, plusTwo),
            updatedAtUtc: new DateTimeOffset(2026, 7, 1, 12, 0, 0, plusTwo));

        var errors = _validator.Validate(request);

        Assert.True(errors.ContainsKey("checkIn"));
        Assert.True(errors.ContainsKey("checkOut"));
        Assert.True(errors.ContainsKey("updatedAtUtc"));
    }

    /// <summary>Several problems at once are all reported, not just the first.</summary>
    [Fact]
    public void Validate_MultipleProblems_ReportsAll()
    {
        var errors = _validator.Validate(TestData.Request(supplierId: "", price: -5m));

        Assert.True(errors.ContainsKey("supplierId"));
        Assert.True(errors.ContainsKey("price"));
    }
}
