// The unload dialog's tool rows, refreshed when it opens.
//
// The page's copy is stale by the time anybody reads it: a gcode command is answered when it is
// QUEUED, so the post returns and the page re-renders in about a hundred milliseconds while the
// printer still has minutes of unloading ahead. The row it captures says the tool is still loaded -
// true at that instant, wrong by the time the dialog is reopened - and the control strip sits
// outside the polled region on purpose, so nothing ever corrects it.
//
// Fetched on show rather than polled, deliberately. Replacing markup underneath somebody who is
// choosing a radio is the failure the polled region is kept away from this strip to avoid; once, at
// the moment the choice starts, is both fresh and stable.
(function () {
    'use strict';

    function refresh(container) {
        var url = container.getAttribute('data-unload-tools-url');

        if (!url) {
            return;
        }

        // same-origin credentials so the sign-in cookie goes with it, and the redirect guard for the
        // same reason live-region.js has one: the handler is behind [Authorize], and fetch follows a
        // redirect silently, so an expired session would paste the login form into the dialog.
        window.fetch(url, {
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'fetch' },
        }).then(function (response) {
            if (!response.ok || response.redirected) {
                throw new Error('' + response.status);
            }

            return response.text();
        }).then(function (html) {
            container.innerHTML = html;
        }).catch(function () {
            // Left as it was. What the page rendered is still a reading of this printer, merely an
            // older one - and blanking the list would turn a stale dialog into an empty one, which is
            // worse: there would be nothing to choose and no way to tell why.
        });
    }

    document.addEventListener('show.bs.modal', function (event) {
        var container = event.target.querySelector('[data-unload-tools]');

        if (container) {
            refresh(container);
        }
    });
})();
