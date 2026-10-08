using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Printing;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The handlers behind the printer page's self-refreshing blocks, and the one the unload dialog
/// fetches when it opens.
/// </summary>
/// <remarks>
/// <para>
/// <b>These answer with a fragment, not a page, and that is the thing to hold down.</b> The script
/// puts whatever comes back straight into the card, so a handler that quietly started returning a
/// whole layout - or a sign-in page - would fill the status card with a copy of the site rather than
/// fail in any visible way.
/// </para>
/// <para>
/// <b>Which is why the anonymous case is here too.</b> An unauthenticated fetch that redirected to
/// the login form would answer 200 with a login page, and the poll would paste it into the printer
/// page every two seconds.
/// </para>
/// </remarks>
public sealed class PrinterStatusPollTests : IAsyncLifetime
{
    /// <summary>How Razor renders a true boolean attribute, which is how a head's radio is found chosen.</summary>
    private const string Checked = "checked=\"checked\"";

    /// <summary>Likewise for a head that cannot be unloaded.</summary>
    private const string Disabled = "disabled=\"disabled\"";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("status-poll");

    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// The page carries the two regions and the script that drives them, so the poll is wired by what
    /// is served rather than by anything a test has to assume.
    /// </summary>
    [Fact]
    public async Task ThePageDeclaresItsLiveRegions()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-regions@example.com");

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().Contain("data-live-region");
            page.Should().Contain($"handler=Status");
            page.Should().Contain($"handler=Graph");

            // Matched without the extension: asp-append-version rewrites the *filename* rather than
            // adding a query string, so the served tag reads "live-region.ebg0z79z8q.js".
            page.Should().Contain("/js/live-region.");
        }
    }

    /// <summary>
    /// The status handler answers with the card alone. Asserted by what is <em>absent</em>: no
    /// document, and none of the page chrome the layout would bring with it.
    /// </summary>
    [Fact]
    public async Task TheStatusHandlerAnswersAFragment()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-fragment@example.com");

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status");

            fragment.Should().Contain("printer-status", "it is the status card");
            fragment.Should().NotContain("<!DOCTYPE", "a fragment is not a document");
            fragment.Should().NotContain("<nav", "the layout would bring the navbar with it");
        }
    }

    /// <summary>
    /// The queue handler answers the queue and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The boundary is the point.</b> A polled partial may only render state its own handler
    /// loads. The slicer address and the remote-ready switch come from <c>SlicerUrl</c> and
    /// <c>CanManagePrinter</c> - set on the full page load and by no poll - so inside the queue
    /// partial every refresh would blank the address and take the switch away until somebody reloaded.
    /// </remarks>
    [Fact]
    public async Task TheQueueHandlerAnswersOnlyTheQueue()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("queue-fragment@example.com");

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Queue");

            fragment.Should().NotContain("<!DOCTYPE", "a fragment is not a document");
            fragment.Should().NotContain("handler=RemoteReady",
                                         "the ready switch is rendered from CanManagePrinter, which no poll sets");
            fragment.Should().NotContain("compat/octoprint",
                                         "the slicer address is rendered from SlicerUrl, which no poll sets");
        }
    }

    /// <summary>
    /// The card carries the control strip as it would be drawn now, so a page opened while the printer
    /// was away gains the controls when it comes back.
    /// </summary>
    /// <remarks>
    /// <b>The case the carrying exists for.</b> A strip drawn once at load, for a printer that was
    /// reconnecting at that moment, offers Preheat, Cool down and Set ready and nothing else - and went
    /// on offering only those through the whole print that followed, with no Stop on the page.
    /// </remarks>
    [Fact]
    public async Task TheCardCarriesTheControlsOfAPrinterThatComesBack()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("controls-return@example.com", state: Reading(PrinterStatus.Printing));

        using (client)
        {
            string away = CarriedControls(await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status"));

            away.Should().Contain("handler=Preheat\"", "preheating is offered whether or not the printer is there");
            away.Should().NotContain("handler=Stop\"", "a printer that is not connected cannot be sent a stop");
            away.Should().NotContain("handler=Pause\"");

            await ConnectAsync(uuid);

            string back = CarriedControls(await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status"));

            back.Should().Contain("handler=Stop\"", "the printer is back, mid-print");
            back.Should().Contain("handler=Pause\"", "and printing, which is when firmware takes a pause");
        }
    }

    /// <summary>
    /// Pause and Resume are offered by firmware's own rule for each, read from the reported state;
    /// Stop is offered whatever the state, for as long as the printer is connected.
    /// </summary>
    [Theory]
    [InlineData(PrinterStatus.Printing, true, false)]
    [InlineData(PrinterStatus.Paused, false, true)]
    [InlineData(PrinterStatus.Attention, false, false)]
    [InlineData(PrinterStatus.Idle, false, false)]
    public async Task PauseAndResumeFollowTheReportedState(PrinterStatus status, bool pause, bool resume)
    {
        (Guid uuid, HttpClient client) = await SeedAsync($"controls-{status}@example.com", state: Reading(status));

        using (client)
        {
            await ConnectAsync(uuid);

            string controls = CarriedControls(await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status"));

            controls.Contains("handler=Pause\"", StringComparison.Ordinal).Should().Be(pause, $"firmware pauses only a printing printer, and this one is {status}");
            controls.Contains("handler=Resume\"", StringComparison.Ordinal).Should().Be(resume, $"firmware resumes only a paused printer, and this one is {status}");
            controls.Should().Contain("handler=Stop\"", "a stop is offered on the connection alone, and refused by the printer if it has nothing to stop");
        }
    }

    /// <summary>
    /// The light is offered to a printer built with the strips or one that has reported a brightness,
    /// starting at what it last reported - and an XL, which never reports one, says so.
    /// </summary>
    [Theory]
    [InlineData("7.1.0", null, true, false, "100")]
    [InlineData("7.1.0", 35, true, false, "35")]
    [InlineData("3.1.0", null, true, true, "100")]
    [InlineData(null, 35, true, false, "35")]
    [InlineData("1.3.5", null, false, false, null)]
    public async Task TheLightFollowsTheModelOrAReport(string? model, int? reported, bool offered, bool unreported, string? value)
    {
        PrinterLiveState state = Reading(PrinterStatus.Printing);
        state.ChamberLedIntensity = reported;

        (Guid uuid, HttpClient client) = await SeedAsync($"lighting-{model}-{reported}@example.com", state: state, model: model);

        using (client)
        {
            await ConnectAsync(uuid);

            string controls = CarriedControls(await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status"));

            controls.Contains("handler=Lighting\"", StringComparison.Ordinal).Should().Be(offered, "offered mid-print too, as firmware takes it in any state");
            controls.Contains("does not report how bright", StringComparison.Ordinal).Should().Be(unreported);

            if (value is not null)
            {
                Regex.Match(controls, "<input[^>]*id=\"lighting\"[^>]*>").Value.Should().Contain($"value=\"{value}\"");
            }
        }
    }

    /// <summary>A printer that is not connected is offered no light, whatever it is.</summary>
    [Fact]
    public async Task ADisconnectedPrinterIsOfferedNoLight()
    {
        PrinterLiveState state = Reading(PrinterStatus.Idle);
        state.ChamberLedIntensity = 35;

        (Guid uuid, HttpClient client) = await SeedAsync("lighting-away@example.com", state: state, model: "7.1.0");

        using (client)
        {
            string controls = CarriedControls(await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status"));

            controls.Should().NotContain("handler=Lighting\"");
        }
    }

    /// <summary>
    /// The strip the card carries offers the filament presets, which the full page load used to be
    /// the only thing to read.
    /// </summary>
    /// <remarks>
    /// A carried strip may only render what the status handler loads. Without the presets it would
    /// draw the preheat control with an empty list, and put that in place of the working one the first
    /// time anything else about the strip changed.
    /// </remarks>
    [Fact]
    public async Task TheCarriedStripOffersThePresets()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("controls-presets@example.com", state: Reading(PrinterStatus.Idle));

        using (client)
        {
            string controls = CarriedControls(await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status"));

            controls.Should().Contain("<option value=\"PLA\"");
            controls.Should().Contain("<option value=\"PETG\"");
        }
    }

    /// <summary>
    /// A printer with live state says what it is doing, in words, rather than as an enum member.
    /// </summary>
    [Fact]
    public async Task TheCardReportsWhatThePrinterIsDoing()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-words@example.com", state: new PrinterLiveState
        {
            Status = PrinterStatus.Printing,
            Progress = 64,
            TimePrinting = 7680,
            TimeRemaining = 4320,
            NozzleTemperature = 215,
            TargetNozzleTemperature = 215,
            BedTemperature = 54,
            TargetBedTemperature = 60,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status");

            fragment.Should().Contain("Printing");
            fragment.Should().Contain("64", "the progress figure is on the card");
            fragment.Should().Contain("at target", "the nozzle has arrived");
            fragment.Should().Contain("heating to", "and the bed has not");
        }
    }

    /// <summary>
    /// <b>The firmware is named beside the team, not on the card</b>: a standing fact about the printer
    /// rather than what it is doing, so it stays out of the part that refreshes every couple of
    /// seconds. Without the build number, which the tooltip keeps, and for whoever manages the
    /// printer it is the way to the firmware page.
    /// </summary>
    [Fact]
    public async Task TheFirmwareIsNamedBesideTheTeamAndNotOnTheCard()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-firmware@example.com",
                                                         state: new PrinterLiveState { LastSeenAt = DateTimeOffset.UtcNow },
                                                         firmware: "6.8.1+12345");

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status");

            page.Should().MatchRegex($@"&middot;\s*<a[^>]*href=""/Printers/Firmware/{uuid}""[^>]*>Firmware 6\.8\.1</a>",
                                     "beside the team, and a manager's way to the firmware page");
            page.Should().Contain("title=\"6.8.1&#x2B;12345\"", "and the whole version is in its tooltip, its plus encoded");
            fragment.Should().NotContain("Firmware 6.8.1", "the card refreshes, and the version has no need to");
            fragment.Should().Contain("Updated", "the age stays on the card");
        }
    }

    /// <summary>
    /// A printer that has never reported a version says nothing about one - no empty label, and no way
    /// to a firmware page that could not match an image to it yet.
    /// </summary>
    [Fact]
    public async Task NothingIsSaidOfAFirmwareNeverReported()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-no-firmware@example.com",
                                                         state: new PrinterLiveState { LastSeenAt = DateTimeOffset.UtcNow });

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().NotContain("/Printers/Firmware/");
            page.Should().NotMatchRegex(@"&middot;\s*<(a|span)[^>]*>Firmware", "there is no version to sit beside the team");
        }
    }

    /// <summary>
    /// A printer waiting for somebody says what for, in the fragment the page refreshes.
    /// </summary>
    /// <remarks>
    /// <b>The gap that prompted the feature</b> (Henrik, 2026-08-29): a red "Waiting for you" with
    /// nothing under it. The state word was never the missing part - the reason was, and it was
    /// already arriving and already stored where nothing rendered it.
    /// </remarks>
    [Fact]
    public async Task AWaitingPrinterSaysWhatItIsWaitingFor()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-attention@example.com", state: new PrinterLiveState
        {
            Status = PrinterStatus.Attention,
            AttentionCode = 23829,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status");

            fragment.Should().Contain("Please replace filament.",
                                      "this is the reason the badge could not give");
            fragment.Should().NotContain("The printer says",
                                         "for an ordinary attention the words are our catalogue's, not the printer's");
            fragment.Should().NotContain("23829",
                                         "the code is not furniture for the card - the sentence is the point");
        }
    }

    /// <summary>
    /// <b>And a printer that is no longer waiting explains nothing</b>, however recently it was.
    /// </summary>
    /// <remarks>
    /// The stored code outlives its dialog by design - it is cleared by the next state change, which
    /// arrives as an event while the status word can arrive by telemetry. For the moment the two
    /// disagree, the sentence must not be shown: a runout explained under a "Printing" badge reads
    /// as a printer that is both fine and not.
    /// </remarks>
    [Fact]
    public async Task APrinterThatIsPrintingAgainExplainsNothing()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-attention-stale@example.com", state: new PrinterLiveState
        {
            Status = PrinterStatus.Printing,
            AttentionCode = 23829,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Status");

            fragment.Should().NotContain("Please replace filament.",
                                         "the dialog is gone even if the code has not been cleared yet");
        }
    }

    /// <summary>The graph handler answers a fragment too, and the fragment is the drawing.</summary>
    [Fact]
    public async Task TheGraphHandlerAnswersAnSvg()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-graph@example.com", state: new PrinterLiveState
        {
            Status = PrinterStatus.Printing,
            LastSeenAt = DateTimeOffset.UtcNow,
        }, samples: 300);

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Graph");

            fragment.Should().Contain("<svg");
            fragment.Should().Contain("printer-graph-nozzle");
            fragment.Should().NotContain("<!DOCTYPE");
        }
    }

    /// <summary>
    /// A printer that has reported nothing gets a sentence rather than an empty pair of axes.
    /// </summary>
    [Fact]
    public async Task AGraphWithNoReadingsSaysSo()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-graph-empty@example.com");

        using (client)
        {
            string fragment = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Graph");

            fragment.Should().NotContain("<svg");
            fragment.Should().Contain("no temperatures");
        }
    }

    /// <summary>
    /// Signed out, the poll is refused rather than answered with a login page it would then paste
    /// into the card.
    /// </summary>
    [Fact]
    public async Task AnAnonymousPollIsNotGivenALoginPage()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("status-anon@example.com");

        client.Dispose();

        using HttpClient anonymous = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await anonymous.GetAsync($"/Printers/Detail/{uuid}?handler=Status",
                                                                      TestContext.Current.CancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);

        if (response.Content.Headers.ContentLength is > 0)
        {
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            body.Should().NotContain("printer-status", "an unauthenticated caller is never handed the card");
        }
    }

    /// <summary>A uuid the caller cannot read is a 404 here, as everywhere else on this page.</summary>
    [Theory]
    [InlineData("Status")]
    [InlineData("Tools")]
    public async Task AnUnknownPrinterIsNotFound(string handler)
    {
        (Guid _, HttpClient client) = await SeedAsync("status-unknown@example.com");

        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/Printers/Detail/{Guid.NewGuid()}?handler={handler}", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// The unload dialog's rows answer for each head as it is when the dialog opens, down to the
    /// moment the last one is emptied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The last step is the one the handler exists for.</b> The dialog is refetched on open because
    /// the page's copy goes stale while an unload runs, and the unload that empties the last loaded
    /// head is the one after which somebody is most likely to look again. A fragment that failed
    /// there would leave the script showing the stale list, still offering the filament that has
    /// just come out.
    /// </para>
    /// <para>
    /// The live state is changed in the database between fetches, so each answer can only have come
    /// from reading it again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheToolsHandlerAnswersEachHeadAsItIsNow()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("tools-fresh@example.com", Toolchanger("PLA", "PETG"));

        using (client)
        {
            string loaded = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Tools");

            loaded.Should().NotContain("<!DOCTYPE", "a fragment is not a document");
            loaded.Should().Contain("PETG");
            Head(loaded, 1).Should().Contain(Checked, "the first loaded head is the default choice");
            Head(loaded, 2).Should().NotContain(Disabled);

            await EmptyHeadAsync(uuid, 1);
            string oneLeft = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Tools");

            Head(oneLeft, 1).Should().Contain(Disabled, "an empty head is listed and inert");
            Head(oneLeft, 2).Should().Contain(Checked, "the choice moves to the head that still has filament");

            await EmptyHeadAsync(uuid, 2);
            string noneLeft = await GetAsync(client, $"/Printers/Detail/{uuid}?handler=Tools");

            Head(noneLeft, 1).Should().Contain(Disabled);
            Head(noneLeft, 2).Should().Contain(Disabled);
            noneLeft.Should().NotContain(Checked, "there is nothing left to choose");
        }
    }

    /// <summary>
    /// A toolchanger with every head empty still has a printer page.
    /// </summary>
    /// <remarks>
    /// The unload dialog is rendered for anybody who may control a printer with more than one tool,
    /// whether or not anything is loaded - only its trigger waits for filament - so the dialog's rows
    /// are drawn on this page too, and an empty machine is an ordinary state for one.
    /// </remarks>
    [Fact]
    public async Task AToolchangerWithEveryHeadEmptyStillHasAPage()
    {
        (Guid uuid, HttpClient client) = await SeedAsync("tools-empty@example.com", Toolchanger(null, null));

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().Contain("data-unload-tools", "the owner may control the printer, so the dialog is drawn");
            Head(page, 1).Should().Contain(Disabled);
            Head(page, 2).Should().Contain(Disabled);
        }
    }

    /// <summary>A single-tool printer's live state in <paramref name="status"/>, with PLA loaded.</summary>
    private static PrinterLiveState Reading(PrinterStatus status)
    {
        return new PrinterLiveState
        {
            Status = status,
            Material = "PLA",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>The control strip a status fragment carries, as rendered.</summary>
    private static string CarriedControls(string fragment)
    {
        Match carried = Regex.Match(fragment, "<template data-live-for=\"printer-controls\">(.*?)</template>", RegexOptions.Singleline);

        carried.Success.Should().BeTrue("the card carries the control strip on every poll");

        return carried.Groups[1].Value;
    }

    /// <summary>
    /// Puts the printer in the connection registry, which is all the page asks of a connection. These
    /// tests render a page and send the printer nothing.
    /// </summary>
    private async Task ConnectAsync(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = await context.Printers
                                     .Where(printer => printer.Uuid == uuid)
                                     .Select(printer => printer.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        _factory.Services.GetRequiredService<PrinterConnectionRegistry>().Register(printerId, new OpenLink(), overPlaintext: false);
    }

    /// <summary>A live state for an idle toolchanger holding <paramref name="materials"/>, one per head.</summary>
    private static PrinterLiveState Toolchanger(params string?[] materials)
    {
        PrinterLiveState state = new()
        {
            Status = PrinterStatus.Idle,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        for (int slot = 1; slot <= materials.Length; slot++)
        {
            state.Slots.Add(new PrinterLiveSlotState { SlotNumber = slot, Material = materials[slot - 1], Temperature = 25 });
        }

        return state;
    }

    /// <summary>The unload dialog's radio for one head, as rendered.</summary>
    private static string Head(string html, int toolNumber)
    {
        Match input = Regex.Match(html, $"<input[^>]*id=\"unload-tool-{toolNumber}\"[^>]*>");

        input.Success.Should().BeTrue($"head {toolNumber} is listed whatever it holds");

        return input.Value;
    }

    /// <summary>What an unload leaves behind, written the way telemetry would write it.</summary>
    private async Task EmptyHeadAsync(Guid uuid, int slot)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = await context.Printers
                                     .Where(printer => printer.Uuid == uuid)
                                     .Select(printer => printer.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        int updated = await context.PrinterLiveSlotStates
                                   .Where(state => state.PrinterId == printerId && state.SlotNumber == slot)
                                   .ExecuteUpdateAsync(set => set.SetProperty(state => state.Material, (string?)null),
                                                       TestContext.Current.CancellationToken);

        updated.Should().Be(1, "the head exists, so the unload has something to empty");
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(Guid uuid, HttpClient client)> SeedAsync(string email,
                                                                 PrinterLiveState? state = null,
                                                                 int samples = 0,
                                                                 string? model = null,
                                                                 string? firmware = null)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        Guid uuid = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = uuid,
            TeamId = membership.TeamId,
            Name = "Garage MK3.5",
            Model = model,
            Firmware = firmware,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (state is not null)
        {
            state.PrinterId = printer.Id;

            foreach (PrinterLiveSlotState slot in state.Slots)
            {
                slot.PrinterId = printer.Id;
            }

            context.PrinterLiveStates.Add(state);
        }

        for (int second = 0; second < samples; second++)
        {
            context.TelemetrySamples.Add(new TelemetrySample
            {
                PrinterId = printer.Id,
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(-samples + second),
                Status = PrinterStatus.Printing,
                NozzleTemperature = 200 + (second / 100f),
                BedTemperature = 60,
                TargetNozzleTemperature = 215,
                TargetBedTemperature = 60,
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (uuid, client);
    }

    private sealed class OpenLink : IPrinterLink
    {
        public bool IsOpen => true;

        public Task<CommandSendResult> SendAsync(IPrinterIntent intent, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("these tests render a page and send the printer nothing");
        }

        public void Complete()
        {
        }
    }
}
