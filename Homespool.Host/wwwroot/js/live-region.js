// Keeps a server-rendered block of the page current by re-fetching it.
//
// The printer page has two of these: the status card, which refreshes every couple of seconds, and
// the temperature graph, which refreshes far more slowly because its window can be a whole print.
//
// Why HTML rather than JSON. Every word in these blocks is localised and every number is
// culture-formatted, and both belong to the server - answering with data would mean a second copy of
// the vocabulary and the formatting rules living here, kept in step by hand, with the resource files
// unable to see it. Fetching the rendered partial costs one request and no vocabulary at all.
//
// Nothing here is required for the page to work. Without script each region simply keeps what the
// server rendered on load, which is what this page did before any of this existed.
(function () {
    'use strict';

    // How long a region keeps its last good content after a failed refresh before saying so. Longer
    // than several intervals, so one dropped request on a flaky connection does not blank a card that
    // is about to be fine.
    var STALE_AFTER_MS = 30000;

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    function attach(region) {
        var url = region.dataset.liveUrl;
        var interval = parseInt(region.dataset.liveInterval, 10);

        if (!url || !(interval > 0)) {
            return;
        }

        var timer = null;
        var lastGoodAt = Date.now();

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
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'fetch' },
            }).then(function (response) {
                if (!response.ok) {
                    throw new Error('' + response.status);
                }

                // A redirect is not an answer to this question. fetch follows one silently, so a
                // session that has expired comes back as 200 carrying the sign-in page - and without
                // this the card fills with a copy of the login form, every two seconds, on a page
                // that still looks signed in. Seen happening, not imagined: the handlers are behind
                // [Authorize], and that is exactly what they do to an anonymous caller.
                if (response.redirected) {
                    throw new Error('redirected');
                }

                return response.text();
            }).then(function (html) {
                region.innerHTML = html;
                region.removeAttribute('data-live-stale');
                lastGoodAt = Date.now();
            }).catch(function () {
                // Marked rather than emptied. What is on screen was true when it was fetched, and the
                // age the card carries already says how long ago that was - the attribute lets the
                // stylesheet fade it so a page nobody is refreshing does not pass for a live one.
                if (Date.now() - lastGoodAt > STALE_AFTER_MS) {
                    region.setAttribute('data-live-stale', '');
                }
            }).finally(schedule);
        }

        // A tab coming back to the front has been showing something possibly minutes old. Refresh at
        // once rather than waiting out the interval, which for the graph is half a minute.
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                refresh();
            }
        });

        schedule();
    }

    ready(function () {
        var regions = document.querySelectorAll('[data-live-region]');

        for (var index = 0; index < regions.length; index++) {
            attach(regions[index]);
        }
    });
})();
