namespace Mize.Api.Services;

/// <summary>
/// <see cref="IWriteGate"/> backed by a one-slot semaphore. Register it as a singleton: one gate per process, which is
/// adequate for one process over one SQLite file.
/// </summary>
public sealed class WriteGate : IWriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <inheritdoc />
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    /// <summary>Releases the semaphore exactly once, however many times it is disposed.</summary>
    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        /// <summary>Creates a handle for an already-acquired semaphore.</summary>
        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        /// <summary>Releases the semaphore the first time it is called.</summary>
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
