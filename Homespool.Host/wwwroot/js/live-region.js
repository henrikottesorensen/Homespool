// Keeps a server-rendered block of the page current by re-fetching it.
//
// The printer page has several of these: the status card, which refreshes every couple of seconds,
// the temperature graph, which refreshes far more slowly because its window can be a whole print, and
// the plate and the queue between them. The control strip is not one - the card carries it, see
// carry() below.
//
// Why HTML rather than JSON. Every word in these blocks is localised and every number is
// culture-formatted, and both belong to the server - answering with data would mean a second copy of
// the vocabulary and the formatting rules living here, kept in step by hand, with the resource files
// unable to see it. Fetching the rendered partial costs one request and no vocabulary at all.
//
// Nothing here is required for the page to work. Without script each region simply keeps what the
// server rendered on load, which is what this page did before any of this existed.
(function () {
    "use strict";

    // How long a region keeps its last good content after a failed refresh before saying so. Longer
    // than several intervals, so one dropped request on a flaky connection does not blank a card that
    // is about to be fine.
    const STALE_AFTER_MS = 30000;

    // Two renders of the same thing are never byte-identical when a form is in them: the antiforgery
    // token is a fresh ciphertext on every request, whatever the form around it says. Compared as
    // fetched, a region with a button in it - the queue, the printers rack - was rebuilt on every poll,
    // and the "unchanged, skip" below never once fired for either. So the comparison blanks the
    // token's value first. What stays on screen when the swap is skipped is the token from an earlier
    // render, and that is fine: a token stays valid for as long as the cookie beside it does, and
    // nothing about it is single-use.
    const TOKEN_INPUT = /<input[^>]*__RequestVerificationToken[^>]*>/g;

    function comparable(html) {
        return html.replace(TOKEN_INPUT, function (tag) {
            return tag.replace(/value="[^"]*"/, 'value=""');
        });
    }

    // A region can also carry content for a part of the page that is not replaced with it: a
    // <template data-live-for="name"> inside the region, for the element marked
    // data-live-target="name". The printer page's control strip is the one there is. Its filament
    // select would lose a choice if it were rebuilt with the status card every two seconds, but a
    // strip drawn once at load goes on offering what was true then - opened while a printer was
    // reconnecting, it had no Stop for the whole print that followed. So it is replaced only when
    // what it would say now differs from what it says, and never while somebody is in it.
    //
    // What each target is showing, compared as comparable() compares, and anything still waiting for
    // somebody to leave it.
    const carried = new WeakMap();

    function place(target, state) {
        if (state.pending === null || target.contains(document.activeElement)) {
            return;
        }

        // A filament chosen and not yet sent is the reader's, not the page's: carried across by id,
        // where the new strip still offers it.
        const chosen = new Map();
        const before = target.querySelectorAll("select[id]");

        for (let index = 0; index < before.length; index++) {
            chosen.set(before[index].id, before[index].value);
        }

        // So is a slider moved and not yet sent - but only one that has been moved. Its value
        // attribute is the printer's last report, and carrying an untouched slider across would
        // keep a newer report from ever showing.
        const moved = new Map();
        const sliders = target.querySelectorAll("input[type=range][id]");

        for (let index = 0; index < sliders.length; index++) {
            if (sliders[index].value !== sliders[index].defaultValue) {
                moved.set(sliders[index].id, sliders[index].value);
            }
        }

        // And a panel somebody has opened stays open: the new strip is drawn with every panel shut,
        // and a light's panel closing under a reader because a print moved on is the page acting on
        // its own.
        const open = new Set();
        const panels = target.querySelectorAll("details[id][open]");

        for (let index = 0; index < panels.length; index++) {
            open.add(panels[index].id);
        }

        target.innerHTML = state.pending;
        state.shown = comparable(state.pending);
        state.pending = null;

        open.forEach(function (id) {
            const panel = document.getElementById(id);

            if (panel instanceof HTMLDetailsElement && target.contains(panel)) {
                panel.open = true;
            }
        });

        const after = target.querySelectorAll("select[id]");

        for (let index = 0; index < after.length; index++) {
            const select = after[index];
            const value = chosen.get(select.id);

            if (value !== undefined && Array.prototype.some.call(select.options, function (option) {
                return option.value === value;
            })) {
                select.value = value;
            }
        }

        moved.forEach(function (value, id) {
            const slider = document.getElementById(id);

            if (slider && target.contains(slider)) {
                slider.value = value;

                // So anything showing the slider's number follows it, as it would have on a drag.
                slider.dispatchEvent(new Event("input", { bubbles: true }));
            }
        });
    }

    function targetOf(template) {
        return document.querySelector('[data-live-target="' + template.dataset.liveFor + '"]');
    }

    // On the first render, what the region carries and what the target shows were drawn by one request
    // and say the same thing - but not in the same characters, since the target's markup is wrapped in
    // the page's own indentation. So the template is what is remembered, not the target.
    function remember(region) {
        const templates = region.querySelectorAll("template[data-live-for]");

        for (let index = 0; index < templates.length; index++) {
            const target = targetOf(templates[index]);

            if (!target || carried.has(target)) {
                continue;
            }

            const state = { shown: comparable(templates[index].innerHTML), pending: null };

            carried.set(target, state);

            // Leaving the strip is when a change held back for its reader can land. Focus has not
            // arrived anywhere yet when focusout fires, so the check waits a turn for it.
            target.addEventListener("focusout", function () {
                window.setTimeout(function () {
                    place(target, state);
                }, 0);
            });
        }
    }

    function carry(region) {
        remember(region);

        const templates = region.querySelectorAll("template[data-live-for]");

        for (let index = 0; index < templates.length; index++) {
            const target = targetOf(templates[index]);
            const state = target ? carried.get(target) : null;

            if (!state) {
                continue;
            }

            const html = templates[index].innerHTML;

            // Back to what is on screen: anything held back is moot.
            state.pending = comparable(html) === state.shown ? null : html;

            place(target, state);
        }
    }

    function ready(fn) {
        if (document.readyState !== "loading") {
            fn();
        } else {
            document.addEventListener("DOMContentLoaded", fn);
        }
    }

    function attach(region) {
        const url = region.dataset.liveUrl;
        const interval = parseInt(region.dataset.liveInterval, 10);

        if (!url || !(interval > 0)) {
            return;
        }

        let timer = null;
        let lastGoodAt = Date.now();

        // What is on screen, so an unchanged answer can be recognised without touching the DOM.
        let lastHtml = null;

        function schedule() {
            if (timer) {
                window.clearTimeout(timer);
            }

            timer = window.setTimeout(refresh, interval);
        }

        function refresh() {
            // A hidden tab is nobody watching. Polling on regardless would keep a phone's radio
            // awake for a card that is not on screen, and the first refresh after it comes back is
            // what the reader actually sees.
            if (document.hidden) {
                schedule();

                return;
            }

            // same-origin credentials so the sign-in cookie goes with it: these handlers are behind
            // [Authorize] and an anonymous fetch would be redirected to the login page, whose HTML
            // would then be swapped into the card.
            window.fetch(url, {
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" },
            }).then(function (response) {
                if (!response.ok) {
                    throw new Error("" + response.status);
                }

                // A redirect is not an answer to this question. fetch follows one silently, so a
                // session that has expired comes back as 200 carrying the sign-in page - and without
                // this the card fills with a copy of the login form, every two seconds, on a page
                // that still looks signed in. Seen happening, not imagined: the handlers are behind
                // [Authorize], and that is exactly what they do to an anonymous caller.
                if (response.redirected) {
                    throw new Error("redirected");
                }

                return response.text();
            }).then(function (html) {
                region.removeAttribute("data-live-stale");
                lastGoodAt = Date.now();

                // Most polls answer with exactly what is already on screen, and replacing markup with
                // an identical copy is not free: it destroys and rebuilds every node under the cursor.
                // For the queue that means the reorder and remove buttons are pulled out from under a
                // finger mid-press several times a minute. Compare first - without the token, see
                // comparable() - and swap only on a real change.
                const next = comparable(html);

                if (next === lastHtml) {
                    return;
                }

                lastHtml = next;
                region.innerHTML = html;

                carry(region);

                // For anything that takes its cue from what a region now says - the picture beside
                // the camera follows the status card's print. Only on a real change, as above.
                region.dispatchEvent(new CustomEvent("live-region-updated", { bubbles: true }));
            }).catch(function () {
                // Marked rather than emptied. What is on screen was true when it was fetched, and the
                // age the card carries already says how long ago that was - the attribute lets the
                // stylesheet fade it so a page nobody is refreshing does not pass for a live one.
                if (Date.now() - lastGoodAt > STALE_AFTER_MS) {
                    region.setAttribute("data-live-stale", "");
                }
            }).finally(schedule);
        }

        // A tab coming back to the front has been showing something possibly minutes old. Refresh at
        // once rather than waiting out the interval, which for the graph is half a minute.
        document.addEventListener("visibilitychange", function () {
            if (!document.hidden) {
                refresh();
            }
        });

        remember(region);
        schedule();
    }

    ready(function () {
        const regions = document.querySelectorAll("[data-live-region]");

        for (let index = 0; index < regions.length; index++) {
            attach(regions[index]);
        }
    });
})();
