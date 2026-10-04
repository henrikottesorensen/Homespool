// Closes a folded-away control the ways a dropdown closes: Escape, or a click anywhere else.
//
// The markup is a <details data-popout> and opens and closes on its own button without this - with
// scripting off, pressing the button again is the way out. What this adds is that a panel left open
// does not sit over the page waiting to be dismissed the one way the browser offers.
//
// A panel closed without Set puts its form back as the printer last reported it. A slider moved and
// then abandoned would otherwise stay where it was left, and live-region.js carries a moved slider
// across every redraw, so it would stop following the printer for as long as the page is open.
//
// Listened for on the document rather than on each panel, because the printer page's control strip
// is replaced while the page is open, and a listener on the panel it replaced would be gone with it.
(function () {
    "use strict";

    function popouts() {
        return document.querySelectorAll("details[data-popout][open]");
    }

    function reset(popout) {
        const forms = popout.querySelectorAll("form");

        for (let index = 0; index < forms.length; index++) {
            forms[index].reset();
        }

        // reset() fires no input event, so anything showing a slider's number is told by hand.
        const sliders = popout.querySelectorAll("input[type=range]");

        for (let index = 0; index < sliders.length; index++) {
            sliders[index].dispatchEvent(new Event("input", { bubbles: true }));
        }
    }

    // Hung from the button's left edge unless that would run past the row the button sits in, in which
    // case from its right. The row rather than the window, because on a wide screen the page's column
    // ends well before the window does, and a panel hanging out into the margin looks dropped there.
    // Measured with the panel back at the left, so a panel flipped once is not judged by where the
    // flip put it.
    function align(popout) {
        const panel = popout.querySelector(".printer-popout-panel");

        if (!panel || !popout.parentElement) {
            return;
        }

        panel.classList.remove("printer-popout-end");

        if (panel.getBoundingClientRect().right > popout.parentElement.getBoundingClientRect().right) {
            panel.classList.add("printer-popout-end");
        }
    }

    // toggle does not bubble, so it is caught on its way down instead. It fires for every way a panel
    // opens or closes - its button, the two below, and live-region.js reopening one after a redraw.
    document.addEventListener("toggle", function (event) {
        const popout = event.target;

        if (!(popout instanceof HTMLDetailsElement) || !popout.hasAttribute("data-popout")) {
            return;
        }

        if (popout.open) {
            align(popout);
        } else {
            reset(popout);
        }
    }, true);

    document.addEventListener("keydown", function (event) {
        if (event.key !== "Escape") {
            return;
        }

        const open = popouts();

        for (let index = 0; index < open.length; index++) {
            const popout = open[index];

            // Focus goes back to the button, rather than being left on a slider that has just been
            // hidden - which a browser answers by dropping it on the page's body.
            if (popout.contains(document.activeElement)) {
                popout.querySelector("summary").focus();
            }

            popout.open = false;
        }
    });

    document.addEventListener("click", function (event) {
        const open = popouts();

        for (let index = 0; index < open.length; index++) {
            if (!open[index].contains(event.target)) {
                open[index].open = false;
            }
        }
    });
})();
