// Live camera view, behind a button under each picture.
//
// The still that camera.js polls is the default and stays the fallback. This is the opt-in, and
// the server names the transport when asked: an H.264 camera gets WebRTC into a <video> with
// nothing re-encoded anywhere; a JPEG camera gets the relayed multipart stream in the still's own
// <img>, which every browser renders natively. Press the button again, or fail to connect, and the
// still comes back.
//
// Three things about this are deliberate and easy to undo by accident:
//
//   The server decides whether the button exists, and which transport it starts. The camera's
//   codec is probed server-side (see CameraLiveAvailability) - and /live is asked from here rather
//   than answered while the page rendered, because the first ask may need the camera to answer,
//   which it does not do while it is off.
//
//   Failure has a deadline. WebRTC can negotiate successfully and then deliver nothing - it
//   happens whenever the address the browser is told to use is not one it can reach - and a
//   connection in that state reports no error at all. So a timer is the only thing that can
//   notice, and when it fires the picture goes back to the still and says so. A confident black
//   rectangle is the failure this whole feature has to avoid.
//
//   A hidden tab stops watching. The stream server does the work only while somebody is consuming,
//   which is the same principle the polled still follows - a background tab must not hold a camera
//   open for nobody.
(function () {
    'use strict';

    // How long to wait for pictures before giving up and going back to the still. Generous against
    // the measured worst case: an H.264 camera cannot produce anything until its next keyframe, and
    // the one this was built against has a 2.25s GOP.
    var CONNECT_TIMEOUT_MS = 10000;

    // How long to wait for the browser to finish gathering its own addresses before sending the
    // offer. This exchange is one request and one answer with no trickling, so everything has to be
    // in that offer - but on a LAN gathering finishes in milliseconds, and this is only the ceiling
    // for a browser that never says it is done.
    var GATHER_TIMEOUT_MS = 2000;

    // How often to ask the connection whether pictures are still arriving, and how many consecutive
    // silent checks end the view. Six seconds of nothing is well past any keyframe gap and short
    // enough that nobody stares at a frozen frame wondering.
    var STATS_INTERVAL_MS = 2000;
    var STALLED_AFTER_CHECKS = 3;

    // How often to ask an MJPEG <img> whether it has decoded anything yet. Short, because a JPEG
    // stream produces its first frame immediately and this interval is the whole of the delay
    // between the picture appearing and the page admitting it has.
    var MJPEG_POLL_MS = 200;

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    // Resolves once the browser has no more addresses to add, or once waiting has gone on long
    // enough to stop being worth it.
    function gathered(connection) {
        return new Promise(function (resolve) {
            if (connection.iceGatheringState === 'complete') {
                resolve();
                return;
            }

            var timer = window.setTimeout(finish, GATHER_TIMEOUT_MS);

            function finish() {
                window.clearTimeout(timer);
                connection.removeEventListener('icegatheringstatechange', check);
                resolve();
            }

            function check() {
                if (connection.iceGatheringState === 'complete') {
                    finish();
                }
            }

            connection.addEventListener('icegatheringstatechange', check);
        });
    }

    function attach(view) {
        var controls = view.parentElement.querySelector('.camera-live-controls');

        if (!controls) {
            return;
        }

        // In the picture now, not in the controls row - the row keeps only the note.
        var button = view.querySelector('.camera-live-toggle');

        if (!button) {
            return;
        }

        var note = controls.querySelector('.camera-live-note');
        var image = view.querySelector('.camera-image');
        var video = view.querySelector('.camera-live-video');
        var status = view.querySelector('.camera-status');

        // The same caption the still uses for its age. While live it says so instead, and the poller
        // is paused, so the two never write to it at once.
        var age = view.parentElement.querySelector('.camera-age');

        var connection = null;
        var deadline = null;
        var stats = null;

        // Which way the server said to watch - 'webrtc' or 'mjpeg' - decided there from the
        // camera's probed codec. For 'mjpeg' the still's own <img> is pointed at the relayed
        // multipart stream, which every browser renders natively.
        var transport = null;
        var streaming = false;

        // The MJPEG path's poll for "has a frame decoded yet". Separate from the WebRTC path's
        // stats interval because they watch different things and stop() must clear both.
        var framePoll = null;

        function say(text) {
            note.textContent = text || '';
        }

        var play = button.querySelector('.camera-live-play');
        var stopIcon = button.querySelector('.camera-live-stop');

        // The button used to be its own status display: its text said watch, stop, connecting,
        // failed. As an icon in the picture it has no room for that, so the label becomes the
        // accessible name and anything that is news goes to the note under the frame.
        //
        // aria-label and title together on purpose - the first is what a screen reader announces,
        // the second is what a pointer reveals, and an icon-only control needs both.
        function label(text, showStop) {
            button.setAttribute('aria-label', text);
            button.setAttribute('title', text);

            play.classList.toggle('d-none', !!showStop);
            stopIcon.classList.toggle('d-none', !showStop);
        }

        // Everything that undoes a live view, in one place: called by the stop button, by the
        // failure deadline, by a connection that drops, and by the tab being hidden. Doing it in
        // four places is how one of them ends up leaving a dead <video> on screen.
        function stop(message) {
            if (deadline) {
                window.clearTimeout(deadline);
                deadline = null;
            }

            if (stats) {
                window.clearInterval(stats);
                stats = null;
            }

            if (framePoll) {
                window.clearInterval(framePoll);
                framePoll = null;
            }

            age.textContent = '';
            age.classList.remove('text-success', 'fw-semibold');

            if (connection) {
                connection.close();
                connection = null;
            }

            if (streaming) {
                // Dropping the src is what closes the connection, which is what lets the relay and
                // then the sidecar release the camera. The poller puts its next still back in.
                streaming = false;
                image.onload = null;
                image.onerror = null;
                image.removeAttribute('src');
                image.classList.add('d-none');
            }

            video.srcObject = null;
            video.classList.add('d-none');

            // Only if there is one to show. An img with no src draws its alt text into an empty
            // panel, which is the rendering defect this page has already been caught by once.
            if (image.getAttribute('src')) {
                image.classList.remove('d-none');
            }

            // Handed back to the poller, which owns whether this reads "Capturing…" or nothing.
            status.classList.remove('d-none');

            button.disabled = false;
            label(button.dataset.labelWatch, false);
            say(message);

            // Hands the picture back to the poller, which restarts from wherever it was.
            view.dispatchEvent(new CustomEvent('camera-live-stopped'));
        }

        function watching() {
            if (deadline) {
                window.clearTimeout(deadline);
                deadline = null;
            }

            // Only now is the still redundant. Swapping earlier would show an empty video element
            // for as long as the first keyframe takes, which is the part that looks broken.
            image.classList.add('d-none');
            video.classList.remove('d-none');

            // A class rather than the hidden attribute, because this element carries d-flex and
            // Bootstrap's display utilities beat hidden - the same trap that once left alt text
            // sitting in an empty panel. d-none sorts last among them, so it is the one that wins.
            status.classList.add('d-none');

            button.disabled = false;
            label(button.dataset.labelStop, true);
            say('');

            watchForPictures();
        }

        // Says "live" only while bytes are actually arriving, and takes the view down when they stop.
        //
        // This is the whole reason there is an indicator at all. A <video> showing a printer that is
        // not moving is indistinguishable from the still it replaced - and a label that simply said
        // "live" because the handshake succeeded would be the same lie the age rule exists to
        // prevent, one layer up. WebRTC can also half-die: the connection stays "connected" while
        // media stops, which no event reports. Counting bytes is the only thing that notices.
        function watchForPictures() {
            var seen = 0;
            var silent = 0;

            stats = window.setInterval(function () {
                if (!connection) {
                    return;
                }

                connection.getStats(null).then(function (report) {
                    var bytes = 0;

                    report.forEach(function (entry) {
                        if (entry.type === 'inbound-rtp' && entry.kind === 'video') {
                            bytes += entry.bytesReceived || 0;
                        }
                    });

                    if (bytes > seen) {
                        seen = bytes;
                        silent = 0;
                        age.textContent = button.dataset.labelLive;
                        age.classList.add('text-success', 'fw-semibold');
                        return;
                    }

                    silent++;

                    if (silent >= STALLED_AFTER_CHECKS) {
                        // Back to the still, which at least tells the truth about how old it is.
                        stop(button.dataset.labelStalled);
                    }
                }).catch(function () {
                    // A closed connection answers nothing; stop() has already tidied up.
                });
            }, STATS_INTERVAL_MS);
        }

        async function start() {
            button.disabled = true;
            label(button.dataset.labelStop, true);
            say(button.dataset.labelConnecting);
            say('');

            // Stops the poll before the connection is made rather than after: both ask the stream
            // server for the same camera, and the still's request is what schedules another capture.
            view.dispatchEvent(new CustomEvent('camera-live-started'));

            // No ICE servers: this deployment does not contact a third party to discover addresses,
            // and on a LAN the host candidates both ends already have are what connects.
            connection = new RTCPeerConnection({ iceServers: [] });

            // The deadline covers the whole attempt, not one step of it, because any of them can
            // succeed while the result is still no picture.
            deadline = window.setTimeout(function () {
                stop(button.dataset.labelFailed);
            }, CONNECT_TIMEOUT_MS);

            connection.addEventListener('track', function (event) {
                video.srcObject = event.streams[0];
            });

            connection.addEventListener('connectionstatechange', function () {
                if (!connection) {
                    return;
                }

                if (connection.connectionState === 'connected') {
                    watching();
                } else if (connection.connectionState === 'failed'
                           || connection.connectionState === 'disconnected'
                           || connection.connectionState === 'closed') {
                    stop(button.dataset.labelFailed);
                }
            });

            try {
                // recvonly, and stated rather than implied: this end has no camera and offering to
                // send would ask the browser for permission it has no reason to want.
                connection.addTransceiver('video', { direction: 'recvonly' });

                var offer = await connection.createOffer();
                await connection.setLocalDescription(offer);
                await gathered(connection);

                var response = await fetch(view.dataset.cameraWebrtc, {
                    method: 'POST',
                    cache: 'no-store',
                    credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        type: 'offer',
                        sdp: connection.localDescription.sdp
                    })
                });

                if (!response.ok) {
                    // 409 included: the server re-checks the transport per request, and a refusal
                    // here is per-session (a browser whose offer carries none of the camera's
                    // codecs). The button stays - another browser may succeed.
                    stop(button.dataset.labelFailed);
                    return;
                }

                var answer = await response.json();

                // Closed while the request was in flight - the stop button, or the tab being
                // hidden. Setting a description on a closed connection throws, and the throw would
                // be reported as a failure the viewer did not cause.
                if (!connection) {
                    return;
                }

                await connection.setRemoteDescription({ type: 'answer', sdp: answer.sdp });
            } catch (error) {
                stop(button.dataset.labelFailed);
            }
        }

        // Live view for a JPEG camera: the same <img> the poller fills is pointed at the relayed
        // multipart stream, which every browser renders natively (measured in Safari 26.5 on
        // 2026-08-19, where it was long believed not to work).
        //
        // "Live" here means the picture has a non-zero size, which is as much as an <img> will say:
        // there is no byte counter to watch the way the WebRTC path does, so a stream that stops
        // sending shows a frozen frame rather than being taken down. The server refusing to answer
        // until a frame has actually arrived covers the case that matters - see the Stream action -
        // and the rest is left to the viewer noticing. A stall detector was considered and
        // deliberately not built: the only way to see frames change on an <img> is sampling it
        // through a canvas, which is unverified on the one engine this path exists for, and a wrong
        // detector tears down a working stream - the exact failure this file already had once.
        function startMjpeg() {
            button.disabled = true;
            label(button.dataset.labelStop, true);
            say(button.dataset.labelConnecting);
            say('');

            view.dispatchEvent(new CustomEvent('camera-live-started'));
            streaming = true;

            // The picture is cleared before the stream is attached, and that is load-bearing rather
            // than tidiness: the only thing an <img> will tell you about a multipart stream is its
            // size, and the still it replaces came from the same camera at the same size - so
            // "has a frame arrived" is unanswerable unless naturalWidth is first driven to zero.
            // Measured in Safari 26.5 (2026-08-19): the stream plays, and no load event arrives for
            // it, so comparing sizes reported failure while frames were visibly decoding.
            image.removeAttribute('src');
            image.classList.add('d-none');
            status.classList.remove('d-none');

            function watchingMjpeg() {
                if (deadline) {
                    window.clearTimeout(deadline);
                    deadline = null;
                }

                if (framePoll) {
                    window.clearInterval(framePoll);
                    framePoll = null;
                }

                status.classList.add('d-none');
                image.classList.remove('d-none');
                button.disabled = false;
                label(button.dataset.labelStop, true);
                age.textContent = button.dataset.labelLive;
                age.classList.add('text-success', 'fw-semibold');
                say('');
            }

            deadline = window.setTimeout(function () {
                if (streaming) {
                    stop(button.dataset.labelFailed);
                }
            }, CONNECT_TIMEOUT_MS);

            // Polled rather than waited for, because the event does not come. A multipart <img>
            // fires load per part in Chrome and Firefox and not at all in Safari, so the event is
            // kept as the fast path and a non-zero size is what actually decides - it can only
            // become non-zero here by a frame off this stream having been decoded.
            framePoll = window.setInterval(function () {
                if (streaming && image.naturalWidth > 0) {
                    watchingMjpeg();
                }
            }, MJPEG_POLL_MS);

            image.onload = function () {
                if (streaming) {
                    watchingMjpeg();
                }
            };

            image.onerror = function () {
                if (streaming) {
                    stop(button.dataset.labelFailed);
                }
            };

            image.src = view.dataset.cameraStream;
            image.classList.remove('d-none');
        }

        button.addEventListener('click', function () {
            if (connection || streaming) {
                // A viewer stopping deliberately is not a failure and gets no message.
                stop('');
            } else if (transport === 'mjpeg') {
                startMjpeg();
            } else {
                start();
            }
        });

        document.addEventListener('visibilitychange', function () {
            if (document.hidden && (connection || streaming)) {
                stop('');
            }
        });

        // Whether this camera can be watched is the server's answer, and it can change from no to
        // yes shortly after a restart - the stream server does not know a camera's codec until it
        // has connected to it once. Asked once here; the still is unaffected either way.
        fetch(view.dataset.cameraLive, { cache: 'no-store', credentials: 'same-origin' })
            .then(function (response) {
                return response.ok ? response.json() : null;
            })
            .then(function (option) {
                if (option && option.available) {
                    transport = option.transport;

                    // Two elements now, in two places: the button lives on the picture and the note
                    // under it. Revealing only the row would leave a camera with no way to start.
                    button.classList.remove('d-none');
                    controls.classList.remove('d-none');
                }
            })
            .catch(function () {
                // No button. A page that cannot ask is a page that should not offer.
            });
    }

    ready(function () {
        var views = document.querySelectorAll('[data-camera-live]');

        for (var i = 0; i < views.length; i++) {
            attach(views[i]);
        }
    });
})();
