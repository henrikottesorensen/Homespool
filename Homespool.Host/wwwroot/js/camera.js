// Keeps each camera view on the printer page current.
//
// The server holds the latest frame and refreshes it only while somebody is asking, so this poll is
// both how a picture is fetched and how the server learns anyone is watching. Stop polling and the
// capturing stops with it.
//
// Three states, and the distinction between them is the point rather than decoration:
//
//   capturing   nothing current yet - the server answered 204. Shows no image at all, because a
//               stale one is exactly what the age rule exists to prevent.
//   live        a frame arrived, with its age beside it.
//   unavailable the camera stopped answering. Says so, rather than leaving the last good frame on
//               screen looking like now.
(function () {
    'use strict';

    // Comfortably under the server's own refresh floor, so the poll is bounded by the camera rather
    // than by this. A faster interval would only ask more often for the same frame.
    var INTERVAL_MS = 2000;

    // How long without a frame before the last picture is taken down. Longer than one acquisition
    // (an RTSP camera takes 2-3s) so a single slow answer does not blank a working view.
    var UNAVAILABLE_AFTER_MS = 15000;

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    function describeAge(captured) {
        var seconds = Math.max(0, Math.round((Date.now() - captured) / 1000));

        if (seconds < 2) {
            return 'live';
        }

        return seconds + 's ago';
    }

    function attach(view) {
        var url = view.dataset.cameraFrame;
        var image = view.querySelector('.camera-image');
        var status = view.querySelector('.camera-status');
        var age = view.parentElement.querySelector('.camera-age');

        var objectUrl = null;
        var lastFrameAt = 0;
        var stopped = false;

        function show(text) {
            status.textContent = text;
            status.hidden = false;
        }

        function poll() {
            if (stopped) {
                return;
            }

            fetch(url, { cache: 'no-store', credentials: 'same-origin' })
                .then(function (response) {
                    if (response.status === 204) {
                        // Asked for, not ready. Only complain once it has been quiet long enough to
                        // mean something.
                        if (!lastFrameAt) {
                            show('Capturing…');
                        }
                        return null;
                    }

                    if (!response.ok) {
                        throw new Error('status ' + response.status);
                    }

                    return response.blob();
                })
                .then(function (blob) {
                    if (!blob || stopped) {
                        return;
                    }

                    var next = URL.createObjectURL(blob);

                    // Revoke only after the new one is showing, or the image blinks empty between
                    // frames.
                    var previous = objectUrl;
                    image.src = next;
                    image.hidden = false;
                    objectUrl = next;

                    if (previous) {
                        URL.revokeObjectURL(previous);
                    }

                    lastFrameAt = Date.now();
                    status.hidden = true;
                })
                .catch(function () {
                    // Network or server failure. Left to the staleness check below rather than
                    // blanking immediately - one failed poll is not an unavailable camera.
                })
                .then(function () {
                    if (stopped) {
                        return;
                    }

                    if (lastFrameAt) {
                        var stale = Date.now() - lastFrameAt > UNAVAILABLE_AFTER_MS;

                        if (stale) {
                            // Take the picture down. Leaving it up is the failure this whole design
                            // is about: an old photograph that looks like the present.
                            image.hidden = true;
                            image.removeAttribute('src');
                            show('Camera not answering');
                            age.textContent = '';
                        } else {
                            age.textContent = describeAge(lastFrameAt);
                        }
                    }

                    window.setTimeout(poll, INTERVAL_MS);
                });
        }

        // Stop while the tab is hidden. The server captures only when asked, so a background tab
        // would otherwise keep a camera awake for nobody.
        document.addEventListener('visibilitychange', function () {
            if (document.hidden) {
                stopped = true;
            } else if (stopped) {
                stopped = false;
                poll();
            }
        });

        poll();
    }

    ready(function () {
        var views = document.querySelectorAll('[data-camera-frame]');

        for (var i = 0; i < views.length; i++) {
            attach(views[i]);
        }
    });
})();
