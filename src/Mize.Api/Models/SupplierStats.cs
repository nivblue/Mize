namespace Mize.Api.Models;

/// <summary>Per-outcome counters for a supplier.</summary>
/// <param name="Created">Requests that inserted a new reservation.</param>
/// <param name="Updated">Requests that updated an existing reservation.</param>
/// <param name="Unchanged">Requests that were exact duplicates (nothing written).</param>
public sealed record IngestBreakdown(long Created, long Updated, long Unchanged);

/// <summary>Body returned by <c>GET /api/reservations/stats/{supplierId}</c>.</summary>
/// <param name="SupplierId">The supplier the numbers belong to.</param>
/// <param name="Ingested">Accepted requests that were ingested: created + updated + unchanged.</param>
/// <param name="Throttled">Requests rejected by the rate limiter.</param>
/// <param name="Breakdown">Per-outcome detail.</param>
public sealed record SupplierStats(string SupplierId, long Ingested, long Throttled, IngestBreakdown Breakdown);
