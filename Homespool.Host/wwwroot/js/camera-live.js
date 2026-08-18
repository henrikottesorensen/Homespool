// Live camera view, over WebRTC, behind a button under each picture.
//
// The still that camera.js polls is the default and stays the fallback. This is the opt-in: press
// the button and the <img> is replaced by a <video> carrying the camera's own H.264 with nothing
// re-encoded anywhere; press it again, or fail to connect, and the still comes back.
//
// Three things about this are deliberate and easy to undo by accident:
//
//   The server decides whether the button exists. WebRTC does not carry the JPEG a USB webcam
//   produces, and only the stream server knows a camera's codec - so /live is asked rather than
//   guessed, and it is asked from here rather than answered while the page rendered, because a
//   camera the sidecar has not yet connected to reports no codec at all.
//
//   Failure has a deadline. This is the one transport that can negotiate successfully and then
//   deliver nothing - it happens whenever the address the browser is told to use is not one it can
//   reach - and a WebRTC connection in that state reports no error at all. So a timer is the only
//   thing that can notice, and when it fires the picture goes back to the still and says so. A
//   confident black rectangle is the failure this whole feature has to avoid.
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

        var button = controls.querySelector('.camera-live-toggle');
        var note = controls.querySelector('.camera-live-note');
        var image = view.querySelector('.camera-image');
        var video = view.querySelector('.camera-live-video');
        var status = view.querySelector('.camera-status');

        var connection = null;
        var deadline = null;

        function say(text) {
            note.textContent = text || '';
        }

        // Everything that undoes a live view, in one place: called by the stop button, by the
        // failure deadline, by a connection that drops, and by the tab being hidden. Doing it in
        // four places is how one of them ends up leaving a dead <video> on screen.
        function stop(message) {
            if (deadline) {
                window.clearTimeout(deadline);
                deadline = null;
            }

            if (connection) {
                connection.close();
                connection = null;
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
            button.textContent = button.dataset.labelWatch;
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
            button.textContent = button.dataset.labelStop;
            say('');
        }

        async function start() {
            button.disabled = true;
            button.textContent = button.dataset.labelConnecting;
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
                    // 409 is the server saying this camera can never be watched live - its video is
                    // JPEG, which WebRTC does not carry. It is the only moment that can be known,
                    // because nothing reports a camera's codec until something consumes it. So the
                    // button goes away rather than staying to fail again.
                    if (response.status === 409) {
                        stop(button.dataset.labelUnsupported);
                        controls.classList.add('d-none');
                        return;
                    }

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

        button.addEventListener('click', function () {
            if (connection) {
                // A viewer stopping deliberately is not a failure and gets no message.
                stop('');
            } else {
                start();
            }
        });

        document.addEventListener('visibilitychange', function () {
            if (document.hidden && connection) {
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
