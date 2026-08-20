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

        // Set while camera-live.js is showing live video from the same camera. Both paths ask the
        // stream server for the same source and a poll is what schedules another capture, so polling
        // underneath a live view would pay for a picture nobody is looking at.
        var yielded = false;

        // The pending poll, so that resuming can cancel it first. Without this, resuming while one
        // is still queued leaves two chains running against the same camera for the life of the
        // page - each scheduling its own successor, so it never settles back to one. The hidden-tab
        // path could already do it; live view makes it easy, because stopping usually happens within
        // one interval of starting.
        var timer = null;

        function resume() {
            if (timer) {
                window.clearTimeout(timer);
                timer = null;
            }

            poll();
        }

        // d-none rather than the hidden attribute, and this is not a style preference: the element
        // carries d-flex, Bootstrap's display utilities are !important, and they beat hidden - so
        // `status.hidden = true` left every message that had ever been shown painted over the
        // picture for the life of the page. camera-live.js learned this once already; this is the
        // same lesson in the other file.
        function show(text) {
            status.textContent = text;
            status.classList.remove('d-none');
        }

        function hideStatus() {
            status.classList.add('d-none');
        }

        function poll() {
            if (stopped || yielded) {
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
                    // A class rather than the hidden attribute: Bootstrap's display utilities carry
                    // !important and beat it, which showed as the alt text sitting in an empty panel.
                    image.classList.remove('d-none');
                    objectUrl = next;

                    if (previous) {
                        URL.revokeObjectURL(previous);
                    }

                    lastFrameAt = Date.now();
                    hideStatus();
                })
                .catch(function () {
                    // Network or server failure. Left to the staleness check below rather than
                    // blanking immediately - one failed poll is not an unavailable camera.
                })
                .then(function () {
                    // yielded as well as stopped: a poll already in flight when the live view took
                    // over would otherwise finish here and write its own caption over the live one -
                    // and its stale branch removes the img's src, which is the live stream's source.
                    // That is how a picture came to sit under "Camera not answering" beside "live".
                    if (stopped || yielded) {
                        return;
                    }

                    if (lastFrameAt) {
                        var stale = Date.now() - lastFrameAt > UNAVAILABLE_AFTER_MS;

                        if (stale) {
                            // Take the picture down. Leaving it up is the failure this whole design
                            // is about: an old photograph that looks like the present.
                            image.classList.add('d-none');
                            image.removeAttribute('src');
                            show('Camera not answering');
                            age.textContent = '';
                        } else {
                            age.textContent = describeAge(lastFrameAt);
                        }
                    }

                    timer = window.setTimeout(poll, INTERVAL_MS);
                });
        }

        // Stop while the tab is hidden. The server captures only when asked, so a background tab
        // would otherwise keep a camera awake for nobody.
        document.addEventListener('visibilitychange', function () {
            if (document.hidden) {
                stopped = true;
            } else if (stopped) {
                stopped = false;
                resume();
            }
        });

        // Live video has taken over this panel. The last frame is deliberately left where it is
        // rather than blanked: the live view puts its own picture over the top, and if it never
        // manages to, what comes back is a still whose age is still being judged by the rule below.
        view.addEventListener('camera-live-started', function () {
            yielded = true;
        });

        view.addEventListener('camera-live-stopped', function () {
            if (yielded) {
                yielded = false;
                resume();
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
