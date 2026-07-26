using System;
using System.Collections.Generic;

namespace Homespool.FakePrinter;

/// <summary>
/// Everything a <see cref="WebSocketConnector"/> needs to perform the upgrade: the target, the
/// subprotocol, and the headers - <c>Fingerprint</c> (the 16-character form) and <c>Token</c> from
/// <c>UpgradeRequest</c> (connect.cpp:160-171) plus the <c>User-Agent-Printer</c>/<c>-Version</c>
/// pair the firmware's HTTP client stamps on every request (httpc.cpp:218-219).
/// </summary>
/// <param name="Uri">The <c>/p/ws</c> endpoint, <c>ws</c>/<c>wss</c> scheme.</param>
/// <param name="SubProtocol">Always <c>prusa-connect</c>.</param>
/// <param name="Headers">The upgrade headers described above.</param>
public sealed record FakePrinterConnectRequest(Uri Uri, string SubProtocol, IReadOnlyDictionary<string, string> Headers);
