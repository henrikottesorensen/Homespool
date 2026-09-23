// Waits for the service to come back after a restart, then moves on.
//
// It has to see the service go away before it believes it is back. The page asking for the restart is
// answered before the stop begins, so the first probes can reach the old process - still up, still
// healthy, about to leave - and moving on then would land on the proxy's holding page a moment later.
// Down is a failed request or any answer that is not a success, which is what the proxy's 503 is
// while nothing is listening behind it.
//
// Without scripting the page still says what is happening and to reload, so nothing here is needed to
// get back; it only saves doing that by hand.
(function () {
    "use strict";

    const status = document.querySelector("[data-await-restart]");

    if (!status) {
        return;
    }

    const probe = status.getAttribute("data-await-restart");
    const destination = status.getAttribute("data-await-restart-then");
    const interval = 2000;
    let wentDown = false;

    function next() {
        window.setTimeout(check, interval);
    }

    function check() {
        fetch(probe, { cache: "no-store", credentials: "same-origin", redirect: "manual" })
            .then(function (response) {
                if (!response.ok) {
                    wentDown = true;
                    next();

                    return;
                }

                if (wentDown) {
                    window.location.assign(destination);

                    return;
                }

                next();
            })
            .catch(function () {
                wentDown = true;
                next();
            });
    }

    next();
})();
