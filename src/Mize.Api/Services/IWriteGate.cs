namespace Mize.Api.Services;

/// <summary>
/// The single in-process gate that serialises database writes of the reservation flow. The ingest logic holds it
/// during read-decide-write, and the statistics cache holds it while loading a supplier for the first time, so a
/// load can never interleave with a half-finished ingest.
/// </summary>
public interface IWriteGate
{
    /// <summary>Waits for the gate and takes it; disposing the returned object releases it.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A handle that releases the gate when disposed.</returns>
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken);
}
