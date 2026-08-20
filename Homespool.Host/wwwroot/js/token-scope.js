// "Tick all" and "Untick all" on the token form, without the round trip.
//
// Each button underneath is a real submit with a page handler of its own, so with scripting off it
// posts and the page comes back with the boxes set. What this adds is doing it here instead - the
// same outcome, minus a request, and without the token name making a trip through the server on a
// form that has not been finished.
//
// It cancels the submit rather than hiding the button, which is the difference from
// toggle-submit.js: there the no-script path is a second control worth hiding once it is redundant,
// whereas this is one control whose two paths reach the same place. Nothing to hide, and so nothing
// that can be left on screen dead.
(function () {
    "use strict";

    var buttons = document.querySelectorAll("[data-tick-all], [data-untick-all]");

    if (!buttons.length) {
        return;
    }

    Array.prototype.forEach.call(buttons, function (button) {
        var form = button.form;

        if (!form) {
            return;
        }

        // Which of the two this is, read once. The attribute that names the group is also the one
        // that says the direction, so the markup cannot ask for a tick and be wired to an untick.
        var ticking = button.hasAttribute("data-tick-all");

        button.addEventListener("click", function (event) {
            // The group is named by the attribute rather than known here, so the checkbox name stays
            // the view's business - it is the model-bound name, and this file should not be a second
            // place that has to be right about it.
            var name = button.getAttribute(ticking ? "data-tick-all" : "data-untick-all");
            var boxes = form.querySelectorAll("input[type=checkbox][name=\"" + name + "\"]");

            if (!boxes.length) {
                // Whatever this button is for is not on the page. Let the post happen and let the
                // server answer, rather than swallowing the click and doing nothing at all.
                return;
            }

            event.preventDefault();

            Array.prototype.forEach.call(boxes, function (box) {
                if (box.checked === ticking) {
                    return;
                }

                box.checked = ticking;

                // So anything watching the boxes - validation now, a live summary later - sees what
                // clicking each of them by hand would have produced.
                box.dispatchEvent(new Event("change", { bubbles: true }));
            });
        });
    });
})();
