using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// Deserializes every message in a real Buddy capture (<c>websocket.capture</c>) - both telemetry
/// (<see cref="TelemetryDTO"/>) and events (<see cref="EventDTO"/>) - rather than trusting
/// hand-written fixtures alone.
/// </summary>
/// <remarks>
/// <para>
/// The capture is from a Prusa Core One, 2025-12-22, extracted from the raw <c>websucket</c> file
/// this project derives its other fixtures from. Identity/credential fields on the one <c>INFO</c>
/// event it contains - <c>sn</c>, <c>fingerprint</c>, <c>api_key</c>, <c>wifi_ssid</c>,
/// <c>wifi_mac</c> - are redacted; every telemetry value and every other event field is untouched.
/// </para>
/// <para>
/// <b>What this capture does not exercise:</b> the printer is single-tool, has no XL enclosure, no
/// MMU, is not a Prusa iX, and never has a transfer in progress while telemetry is sent, and no
/// telemetry message in it carries <c>command_id</c> (only the interleaved command/event traffic
/// does). So <see cref="TelemetryDTO.Slot"/>, <see cref="TelemetryDTO.Enclosure"/>,
/// <see cref="TelemetryDTO.TransferId"/>, the Prusa-iX fields, and <see cref="TelemetryDTO.CommandId"/>
/// are proven absent-safe here, not proven correct when present. The capture also only ever shows
/// <c>INFO</c>, <c>REJECTED</c>, <c>JOB_INFO</c> and <c>FILE_INFO</c> events - the other 10 event
/// types (including the firmware-only <see cref="Events.CancelableChanged"/>) rest on the firmware
/// source reading in <c>notes/phase-2-dispatch.md</c> alone, same as the untested telemetry fields.
/// </para>
/// </remarks>
public class CaptureReplayTests
{
    /// <summary>Telemetry messages in the capture - i.e. every parsed document without an
    /// <c>"event"</c> property. Asserting this count is what catches a reader that silently stops
    /// early or a preprocessing step that swallows real messages.</summary>
    private const int ExpectedTelemetryCount = 2058;

    /// <summary>The 5 event messages interleaved in the same capture - not telemetry, skipped.</summary>
    private const int ExpectedEventCount = 5;

    [Fact]
    public void EveryTelemetryMessageInTheCaptureDeserializes()
    {
        // Arrange
        CaptureReplayResult result = ReplayCapture();

        // Assert
        result.EventCount.Should().Be(ExpectedEventCount);
        result.TelemetryCount.Should().Be(ExpectedTelemetryCount);
        result.TelemetryFailures.Should().BeEmpty();

        // Spot-check content, not just "didn't throw" - a run of empty DTOs would pass an
        // exception-only assertion.
        result.ChamberLedSeen.Should().BeGreaterThan(0, "Full-mode messages in this capture do carry a "
                                                        + "non-zero chamber.led_intensity, and the fix "
                                                        + "for that field should be exercised by real data");
    }

    /// <summary>
    /// The 5 real event messages - <c>INFO</c>, <c>REJECTED</c>, <c>JOB_INFO</c>, and
    /// <c>FILE_INFO</c> twice - all deserialize into <see cref="EventDTO"/>, and the one
    /// <c>INFO</c> event's <see cref="EventDTO.Data"/> deserializes into
    /// <see cref="InfoEventDataDTO"/> with the fields a real Core One actually sent (the
    /// credential/identity fields were redacted, not removed, so this also confirms redaction
    /// didn't corrupt the surrounding JSON shape).
    /// </summary>
    [Fact]
    public void EveryEventMessageInTheCaptureDeserializes()
    {
        // Arrange
        CaptureReplayResult result = ReplayCapture();

        // Assert
        result.EventFailures.Should().BeEmpty();
        result.EventTypesSeen.Should().Equal(
            Events.Info, Events.Rejected, Events.JobInfo, Events.FileInfo, Events.FileInfo);

        result.InfoData.Should().NotBeNull();
        result.InfoData!.Firmware.Should().Be("6.4.0+11974");
        result.InfoData.PrinterType.Should().Be("7.1.0");
        result.InfoData.Slots.Should().Be(1);
        result.InfoData.Tools.Should().ContainKey("1");
        result.InfoData.NetworkInfo.Should().NotBeNull();

        // The redaction target: values are placeholders, but the fields themselves must still be
        // present and parse as the same shape a live capture would have.
        result.InfoData.SerialNumber.Should().Be("REDACTED-SERIAL-NUMBER");
        result.InfoData.ApiKey.Should().Be("REDACTED-API-KEY");
        result.InfoData.NetworkInfo!.WifiSsid.Should().Be("REDACTED-SSID");
    }

    /// <summary>
    /// Parses the capture the same way <c>WebSocketHandler</c> has to: documents may be
    /// concatenated with no separator, and the interleaved server-&gt;printer command frames
    /// (single type char + 8 hex digits + JSON, e.g. <c>J00000140{...}</c>) are not valid JSON on
    /// their own and are not telemetry, so they are stripped line-by-line before parsing.
    /// </summary>
    private static CaptureReplayResult ReplayCapture()
    {
        string raw = File.ReadAllText("websocket.capture");

        StringBuilder cleaned = new();

        foreach (string line in raw.Split('\n'))
        {
            if (IsCommandFrame(line))
            {
                continue;
            }

            cleaned.Append(line).Append('\n');
        }

        ReadOnlySpan<byte> span = Encoding.UTF8.GetBytes(cleaned.ToString());

        List<string> telemetryFailures = [];
        List<string> eventFailures = [];
        List<Events> eventTypesSeen = [];
        int telemetryCount = 0;
        int eventCount = 0;
        int chamberLedSeen = 0;
        InfoEventDataDTO? infoData = null;

        while (true)
        {
            AdvancePastWhitespace(ref span);

            if (span.IsEmpty)
            {
                break;
            }

            Utf8JsonReader reader = new(span, isFinalBlock: true, default);

            if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? document))
            {
                telemetryFailures.Add("Could not parse remaining input as JSON.");

                break;
            }

            using (document)
            {
                if (document.RootElement.TryGetProperty("event", out _))
                {
                    eventCount++;

                    try
                    {
                        EventDTO? eventDto = JsonSerializer.Deserialize<EventDTO>(document.RootElement);

                        if (eventDto is null)
                        {
                            eventFailures.Add("Deserialized to null: " + document.RootElement.GetRawText());
                        }
                        else
                        {
                            eventTypesSeen.Add(eventDto.EventType);

                            if (eventDto.EventType == Events.Info && eventDto.Data is not null)
                            {
                                infoData = eventDto.Data.Value.Deserialize<InfoEventDataDTO>();
                            }
                        }
                    }
                    catch (JsonException e)
                    {
                        eventFailures.Add($"{e.Message}\n  in: {document.RootElement.GetRawText()}");
                    }
                }
                else
                {
                    telemetryCount++;

                    try
                    {
                        TelemetryDTO? dto = JsonSerializer.Deserialize<TelemetryDTO>(document.RootElement);

                        if (dto is null)
                        {
                            telemetryFailures.Add("Deserialized to null: " + document.RootElement.GetRawText());
                        }
                        else if (dto.Chamber?.LedIntensity is not (null or 0))
                        {
                            chamberLedSeen++;
                        }
                    }
                    catch (JsonException e)
                    {
                        telemetryFailures.Add($"{e.Message}\n  in: {document.RootElement.GetRawText()}");
                    }
                }
            }

            span = span[(int)reader.BytesConsumed..];
        }

        return new CaptureReplayResult(telemetryCount, eventCount, telemetryFailures, chamberLedSeen,
                                        eventFailures, eventTypesSeen, infoData);
    }

    private sealed record CaptureReplayResult(
        int TelemetryCount,
        int EventCount,
        List<string> TelemetryFailures,
        int ChamberLedSeen,
        List<string> EventFailures,
        List<Events> EventTypesSeen,
        InfoEventDataDTO? InfoData);

    private static bool IsCommandFrame(string line)
    {
        if (line.Length < 9 || !"JGFDT".Contains(line[0]))
        {
            return false;
        }

        for (int i = 1; i <= 8; i++)
        {
            if (!Uri.IsHexDigit(line[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void AdvancePastWhitespace(ref ReadOnlySpan<byte> span)
    {
        int i = 0;

        while (i < span.Length && span[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        span = span[i..];
    }
}
