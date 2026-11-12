namespace Mize.Api.Stats;

/// <summary>Writes the not-yet-saved statistics increments to the database.</summary>
public interface IStatsFlusher
{
    /// <summary>
    /// Saves every supplier's unflushed increments in one transaction. When the write fails the increments are kept and
    /// the exception is thrown, so a later call retries with nothing lost.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task FlushAsync(CancellationToken cancellationToken);
}
