// Shows a slider's number beside it while it moves.
//
// A range input has no visible value of its own, so the server renders the number into an <output>
// and this keeps it in step with the thumb. Without script the number is the one the page was drawn
// with and the slider still posts whatever it is set to - the form works either way.
//
// Listened for on the document rather than on each slider, because the printer page's control strip
// is replaced while the page is open, and a listener on the slider it replaced would be gone with it.
(function () {
    "use strict";

    document.addEventListener("input", function (event) {
        const input = event.target;

        if (!(input instanceof HTMLInputElement) || !input.dataset.rangeOutput) {
            return;
        }

        const output = document.getElementById(input.dataset.rangeOutput);

        if (output) {
            output.textContent = input.value;
        }
    });
})();
