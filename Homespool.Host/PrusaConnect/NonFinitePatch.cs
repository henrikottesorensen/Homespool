using System;
using System.Collections.Generic;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// What <see cref="NonFiniteNumberPatcher"/> made of a document it could mend.
/// </summary>
/// <param name="Document">A copy of the document with each non-finite number replaced by its quoted
/// literal and every other byte as it arrived.</param>
/// <param name="Tokens">What was replaced, in document order. Never empty.</param>
public sealed record NonFinitePatch(ReadOnlyMemory<byte> Document, IReadOnlyList<NonFiniteToken> Tokens);
