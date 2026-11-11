namespace Mize.Api.Models;

/// <summary>Outcome of ingesting one reservation.</summary>
public enum IngestStatus
{
    /// <summary>The reservation was new and has been inserted.</summary>
    Created,

    /// <summary>The reservation existed with different details and has been updated.</summary>
    Updated,

    /// <summary>The reservation already existed with identical details; nothing was written.</summary>
    Unchanged
}
