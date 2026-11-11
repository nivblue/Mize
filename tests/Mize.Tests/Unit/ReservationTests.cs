using Mize.Api.Models;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="Reservation"/>.</summary>
public class ReservationTests
{
    /// <summary>All fields are copied across from the request.</summary>
    [Fact]
    public void FromRequest_CopiesAllFields()
    {
        var reservation = Reservation.FromRequest(TestData.Request());

        Assert.Equal("supplier-1", reservation.SupplierId);
        Assert.Equal("res-1", reservation.ReservationId);
        Assert.Equal("room-1", reservation.RoomId);
        Assert.Equal(TestData.CheckIn, reservation.CheckIn);
        Assert.Equal(TestData.CheckOut, reservation.CheckOut);
        Assert.Equal(450.00m, reservation.Price);
        Assert.Equal(TestData.UpdatedAt, reservation.UpdatedAtUtc);
    }

    /// <summary>Timestamps given with an offset are normalised to UTC.</summary>
    [Fact]
    public void FromRequest_NormalisesTimestampsToUtc()
    {
        var local = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var reservation = Reservation.FromRequest(TestData.Request(checkIn: local));

        Assert.Equal(TimeSpan.Zero, reservation.CheckIn.Offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), reservation.CheckIn);
    }

    /// <summary>Equal details give equal reservations (basis of "unchanged" detection); any difference breaks equality.</summary>
    [Fact]
    public void Equality_ReflectsAllDetails()
    {
        Assert.Equal(TestData.Reservation(), TestData.Reservation());
        Assert.NotEqual(TestData.Reservation(), TestData.Reservation(price: 451m));
        Assert.NotEqual(TestData.Reservation(), TestData.Reservation(roomId: "other"));
        Assert.Equal(TestData.Reservation(price: 450m), TestData.Reservation(price: 450.00m));
    }
}
