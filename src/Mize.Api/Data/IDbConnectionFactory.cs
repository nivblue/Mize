using System.Data.Common;

namespace Mize.Api.Data;

/// <summary>Creates open database connections; the single seam to swap the database engine.</summary>
public interface IDbConnectionFactory
{
    /// <summary>Creates and opens a new connection. The caller owns and must dispose it.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection.</returns>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
