// Takes a success alert away once it has been read.
//
// Ordinary pages do not need this: you act, you land on a fresh render, you navigate on, and the
// message goes with the page. The printer page is the one that stays open for the length of a print,
// so an alert rendered at load has nothing to clear it - "Marked ready. The next queued print can
// start." sat there for the whole job it had already started, describing something that had finished
// happening an hour earlier.
//
// Only successes, and that is the point of the split rather than a timing preference. A success
// confirms something you just did and just watched happen; a failure may be the only place the
// printer's own refusal is written down, and taking that away from somebody who looked away would
// lose it. Failures keep their close button and nothing else.
(function () {
    'use strict';

    // Long enough to read a sentence twice without being asked to hurry, short enough that it is gone
    // before the thing it describes stops being recent.
    var DISMISS_AFTER_MS = 12000;

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    ready(function () {
        var alerts = document.querySelectorAll('[data-dismiss-after]');

        for (var index = 0; index < alerts.length; index++) {
            window.setTimeout(function (element) {
                return function () {
                    // Bootstrap's own dismissal, so the fade matches the close button beside it and
                    // the element is removed rather than left hidden in the layout.
                    if (window.bootstrap && window.bootstrap.Alert) {
                        window.bootstrap.Alert.getOrCreateInstance(element).close();
                    } else {
                        element.remove();
                    }
                };
            }(alerts[index]), DISMISS_AFTER_MS);
        }
    });
})();
