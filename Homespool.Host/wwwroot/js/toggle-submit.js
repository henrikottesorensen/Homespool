// A switch that saves itself, so flipping it and meaning it are one action rather than two.
//
// The markup underneath works without this. The checkbox is a real input inside an ordinary form
// with an ordinary submit button, so with scripting off you flip the switch and press Save. What
// this adds is removing the second step - and it removes it by hiding the button here rather than
// in the view, so a browser that never runs this is left with the working two-step form instead of
// a switch that does nothing. That failure is the one worth designing against: a dead control looks
// broken, where a visible Save button merely looks ordinary.
//
// It submits the form rather than sending a background request, deliberately. "Did that save?" is
// then answered the way every other control on these pages answers it - the page comes back with
// its status message - and one code path stays one code path. A fetch would need its own success
// and failure handling to say the same thing, and would be a second way to write the same row.
(function () {
    "use strict";

    var inputs = document.querySelectorAll("[data-submit-on-change]");

    if (!inputs.length) {
        return;
    }

    Array.prototype.forEach.call(inputs, function (input) {
        var form = input.form;

        if (!form) {
            return;
        }

        Array.prototype.forEach.call(form.querySelectorAll("[data-submit-fallback]"), function (button) {
            button.hidden = true;
        });

        input.addEventListener("change", function () {
            // requestSubmit rather than submit: it fires the submit event and runs validation, so
            // the form behaves as though the hidden button had been pressed instead of bypassing
            // everything attached to it.
            if (typeof form.requestSubmit === "function") {
                form.requestSubmit();
            } else {
                form.submit();
            }
        });
    });
})();
