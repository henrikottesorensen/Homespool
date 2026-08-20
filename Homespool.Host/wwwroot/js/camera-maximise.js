// Blows a camera panel up to fill the window, and back.
//
// The one rule this turns on: the picture is never moved. A live MJPEG stream lives in that <img>,
// and reparenting an element restarts its load - which drops the stream, releases the relay's
// connection, and makes the stream server open the camera again. So this only ever toggles a class
// on the panel the picture is already in; nothing is appended anywhere.
//
// Not the Fullscreen API either, deliberately. iOS Safari grants fullscreen to <video> alone, and
// the still and the MJPEG live view are both an <img> - so requestFullscreen would work on a desktop
// and do nothing on the platform this is most wanted on. A class behaves identically for every
// transport on every platform.
//
// The button is hidden in the markup and revealed here, because without scripting it cannot act.
(function () {
    'use strict';

    var OPEN_BODY_CLASS = 'camera-maximised-open';
    var OPEN_VIEW_CLASS = 'camera-view-maximised';

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    function attach(view) {
        var button = view.querySelector('.camera-maximise');

        if (!button) {
            return;
        }

        // Only now is it usable, so only now is it shown.
        button.hidden = false;
        button.classList.remove('d-none');

        function open() {
            view.classList.add(OPEN_VIEW_CLASS);
            document.body.classList.add(OPEN_BODY_CLASS);
            button.setAttribute('aria-label', button.dataset.labelRestore);
            button.title = button.dataset.labelRestore;
        }

        function close() {
            view.classList.remove(OPEN_VIEW_CLASS);
            document.body.classList.remove(OPEN_BODY_CLASS);
            button.setAttribute('aria-label', button.dataset.labelMaximise);
            button.title = button.dataset.labelMaximise;
        }

        function isOpen() {
            return view.classList.contains(OPEN_VIEW_CLASS);
        }

        button.addEventListener('click', function (event) {
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
        view.addEventListener('click', function (event) {
            if (isOpen() && event.target === view) {
                close();
            }
        });

        document.addEventListener('keydown', function (event) {
            if (isOpen() && (event.key === 'Escape' || event.key === 'Esc')) {
                close();
            }
        });

        // A hidden tab already stops the poll and any live view; coming back to a panel still
        // maximised is fine, so nothing is undone here. But a live view that stops for its own
        // reasons hands the panel back to the still, and the maximised state should survive that
        // too - which it does, because it belongs to the panel rather than to either picture.
    }

    ready(function () {
        var views = document.querySelectorAll('[data-camera-frame]');

        for (var i = 0; i < views.length; i++) {
            attach(views[i]);
        }
    });
})();
