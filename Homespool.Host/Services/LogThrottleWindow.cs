using System;

namespace Homespool.Host.Services;

/// <summary>
/// What an elected caller gets to log: whether this is the very first occurrence (log it in full,
/// immediately), how many occurrences the window aggregated, how long the window was, and the
/// exact lifetime total.
/// </summary>
/// <param name="IsFirstOccurrence">True for the first occurrence ever recorded - warn in full
/// detail; there is no window to summarize yet.</param>
/// <param name="Count">Occurrences aggregated since the last elected log line, this one included.</param>
/// <param name="Elapsed">Time since the last elected log line; zero on the first occurrence.</param>
/// <param name="Total">Exact lifetime occurrence count, this one included.</param>
public readonly record struct LogThrottleWindow(bool IsFirstOccurrence, long Count, TimeSpan Elapsed, long Total);
