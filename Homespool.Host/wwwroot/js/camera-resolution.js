// Offers the capture sizes the selected camera actually reports, and nothing else.
//
// The sizes ride on each device's <option> as a data attribute, put there by the page from what the
// stream server enumerated - so choosing a device repopulates the list beside it with no round trip
// and no request. A camera that reports nothing (one plugged in after the stream server started, or
// a deployment where it could not be asked) simply keeps the default entry, which is always valid:
// no size at all is what "whatever the camera provides" means on the wire.
//
// Without this script the page still works: the resolution select holds the default option and the
// form submits an empty value, which is exactly the state a user who expresses no preference wants.
(function () {
    'use strict';

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    function attach(devices) {
        var target = document.getElementById(devices.dataset.resolutionTarget);

        if (!target) {
            return;
        }

        // The first entry is the "camera default" the page rendered, and it is kept across every
        // repopulation - it is the one choice no camera can refuse.
        var fallback = target.options[0];

        function repopulate() {
            var option = devices.options[devices.selectedIndex];
            var sizes = option && option.dataset.sizes ? option.dataset.sizes.split(' ') : [];

            target.textContent = '';
            target.appendChild(fallback);

            for (var i = 0; i < sizes.length; i++) {
                if (!sizes[i]) {
                    continue;
                }

                var entry = document.createElement('option');
                entry.value = sizes[i];
                entry.textContent = sizes[i];
                target.appendChild(entry);
            }
        }

        devices.addEventListener('change', repopulate);
        repopulate();
    }

    ready(function () {
        var selects = document.querySelectorAll('[data-resolution-target]');

        for (var i = 0; i < selects.length; i++) {
            attach(selects[i]);
        }
    });
})();
