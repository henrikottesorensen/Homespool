using System;
using System.Collections.Generic;

using Microsoft.Extensions.Options;

namespace Homespool.Host.Test;

/// <summary>
/// Options doubles for the consumers that read their settings live.
/// </summary>
/// <remarks>
/// <c>Options.Create</c> answers an <see cref="IOptions{TOptions}"/>, which those consumers
/// deliberately no longer take — a value that can never change is the thing the live grade exists to
/// rule out. These are the two shapes they do take.
/// </remarks>
public static class TestOptions
{
    /// <summary>An <see cref="IOptionsSnapshot{TOptions}"/> over one fixed value.</summary>
    /// <typeparam name="T">The options type.</typeparam>
    /// <param name="value">What the snapshot answers.</param>
    /// <returns>The snapshot.</returns>
    public static IOptionsSnapshot<T> Snapshot<T>(T value)
        where T : class
    {
        return new FixedSnapshot<T>(value);
    }

    /// <summary>
    /// An <see cref="IOptionsMonitor{TOptions}"/> whose value can be changed, so a test can assert
    /// that a consumer obeys the change rather than only that it read the value once.
    /// </summary>
    /// <typeparam name="T">The options type.</typeparam>
    /// <param name="value">The starting value.</param>
    /// <returns>The monitor.</returns>
    public static ChangeableMonitor<T> Monitor<T>(T value)
        where T : class
    {
        return new ChangeableMonitor<T>(value);
    }

    private sealed class FixedSnapshot<T> : IOptionsSnapshot<T>
        where T : class
    {
        private readonly T _value;

        public FixedSnapshot(T value)
        {
            _value = value;
        }

        public T Value => _value;

        public T Get(string? name)
        {
            return _value;
        }
    }
}

/// <summary>
/// An options monitor a test can move, with the change notification real consumers register for.
/// </summary>
/// <typeparam name="T">The options type.</typeparam>
public sealed class ChangeableMonitor<T> : IOptionsMonitor<T>
    where T : class
{
    private readonly List<Action<T, string?>> _listeners = [];

    /// <summary>Creates the monitor at a starting value.</summary>
    /// <param name="value">The value to begin with.</param>
    public ChangeableMonitor(T value)
    {
        CurrentValue = value;
    }

    /// <inheritdoc />
    public T CurrentValue { get; private set; }

    /// <inheritdoc />
    public T Get(string? name)
    {
        return CurrentValue;
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);

        return new Subscription(() => _listeners.Remove(listener));
    }

    /// <summary>Moves the value and tells everyone who asked.</summary>
    /// <param name="value">The new value.</param>
    public void Set(T value)
    {
        CurrentValue = value;

        foreach (Action<T, string?> listener in _listeners.ToArray())
        {
            listener(value, Options.DefaultName);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            _dispose();
        }
    }
}
