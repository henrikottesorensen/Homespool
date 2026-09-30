// Blows a camera panel up to fill the window, and back.
//
// The one rule this turns on: the picture is never moved. It does not need to be: a class on the
// panel it already sits in fills the window the same way for a polled still, a relayed MJPEG stream
// and a WebRTC <video>, with nothing to find and put back afterwards. So this only ever toggles a
// class; nothing is appended anywhere.
//
// Not because moving it would restart a live stream: measured in Chromium and WebKit (Playwright's
// builds), moving the <img> elsewhere in the document keeps its stream playing, with no second
// request. Safari itself is unmeasured.
//
// Not the Fullscreen API either, deliberately. iOS Safari grants fullscreen to <video> alone, and
// the still and the MJPEG live view are both an <img> - so requestFullscreen would work on a desktop
// and do nothing on the platform this is most wanted on. A class behaves identically for every
// transport on every platform.
//
// The button is hidden in the markup and revealed here, because without scripting it cannot act.
(function () {
    "use strict";

    const OPEN_BODY_CLASS = "camera-maximised-open";
    const OPEN_VIEW_CLASS = "camera-view-maximised";

    function ready(fn) {
        if (document.readyState !== "loading") {
            fn();
        } else {
            document.addEventListener("DOMContentLoaded", fn);
        }
    }

    function attach(view) {
        const button = view.querySelector(".camera-maximise");

        if (!button) {
            return;
        }

        // Only now is it usable, so only now is it shown.
        button.hidden = false;
        button.classList.remove("d-none");

        function open() {
            view.classList.add(OPEN_VIEW_CLASS);
            document.body.classList.add(OPEN_BODY_CLASS);
            button.setAttribute("aria-label", button.dataset.labelRestore);
            button.title = button.dataset.labelRestore;
        }

        function close() {
            view.classList.remove(OPEN_VIEW_CLASS);
            document.body.classList.remove(OPEN_BODY_CLASS);
            button.setAttribute("aria-label", button.dataset.labelMaximise);
            button.title = button.dataset.labelMaximise;
        }

        function isOpen() {
            return view.classList.contains(OPEN_VIEW_CLASS);
        }

        button.addEventListener("click", function (event) {
            // The panel is inside a figure that may itself be clickable later; keep this local.
            event.stopPropagation();

            if (isOpen()) {
                close();
            } else {
                open();
            }
        });

        // Clicking the backdrop closes, but a click on the picture must not - somebody watching a
        // print will rest a cursor there, and losing the view to that would be its own annoyance.
        view.addEventListener("click", function (event) {
            if (isOpen() && event.target === view) {
                close();
            }
        });

        document.addEventListener("keydown", function (event) {
            if (isOpen() && (event.key === "Escape" || event.key === "Esc")) {
                close();
            }
        });

        // Taken off the page while filling the window - a print starting gives its place to the
        // preview - would leave the page locked under an overlay that is no longer there.
        view.addEventListener("camera-parked", function () {
            if (isOpen()) {
                close();
            }
        });

        // A hidden tab already stops the poll and any live view; coming back to a panel still
        // maximised is fine, so nothing is undone here. But a live view that stops for its own
        // reasons hands the panel back to the still, and the maximised state should survive that
        // too - which it does, because it belongs to the panel rather than to either picture.
    }

    ready(function () {
        const views = document.querySelectorAll("[data-camera-frame]");

        for (let i = 0; i < views.length; i++) {
            attach(views[i]);
        }
    });
})();
