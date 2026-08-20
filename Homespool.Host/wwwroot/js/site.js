// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Dropping a file onto the upload area puts it in the file input, and nothing else.
//
// Deliberately an enhancement rather than an upload path of its own: the drop hands the file to the
// input the form already posts, so submitting still goes through the same handler, the same
// antiforgery token and the same multipart body. With scripting off the picker works exactly as it
// did - which is why this is the whole of the app's JavaScript rather than the start of an uploader.
//
// It also stops at filling the input rather than submitting it. One code path stays one code path,
// and a file dropped by accident does not begin a 300 MB upload before anyone can object.
(function () {
    "use strict";

    var zone = document.querySelector("[data-upload-dropzone]");

    if (!zone) {
        return;
    }

    var input = zone.querySelector("input[type=file]");

    if (!input) {
        return;
    }

    function suppress(event) {
        // Without both of these the browser navigates to the dropped file, and the page is gone.
        event.preventDefault();
        event.stopPropagation();
    }

    ["dragenter", "dragover"].forEach(function (name) {
        zone.addEventListener(name, function (event) {
            suppress(event);
            zone.classList.add("upload-dropzone-active");
        });
    });

    ["dragleave", "dragend", "drop"].forEach(function (name) {
        zone.addEventListener(name, function (event) {
            suppress(event);
            zone.classList.remove("upload-dropzone-active");
        });
    });

    zone.addEventListener("drop", function (event) {
        if (!event.dataTransfer || event.dataTransfer.files.length === 0) {
            return;
        }

        // Rebuilt as a one-file list rather than assigned straight across: the input takes a single
        // file, and handing it a longer list is not something every browser agrees about.
        var single = new DataTransfer();

        single.items.add(event.dataTransfer.files[0]);
        input.files = single.files;

        // So anything watching the input - validation now, a preview later - sees the same event it
        // would have seen had the file been chosen through the picker.
        input.dispatchEvent(new Event("change", { bubbles: true }));
    });
})();

// Start client-side validation, which aspnet-client-validation does not do for itself. Its
// predecessor, jquery.validate.unobtrusive, started on its own - so this call is the entire
// behavioural difference between the two libraries as this application uses them.
//
// Deferred to DOMContentLoaded for two reasons that both matter. _ValidationScriptsPartial renders
// into the Scripts section, which _Layout places *after* this file, so aspnetValidation does not
// exist yet while this line is being read. And the library needs the form in the document to attach
// to it at all.
//
// Guarded because most pages render no form and therefore never load the library. An absent
// validator is the normal case here, not a failure.
//
// If this never runs, validation falls back to the server, which was always the thing that decides:
// the page posts, is rejected, and comes back with the same messages rendered by the same tag
// helpers. The cost of failure is a round trip, not an accepted bad value.
document.addEventListener("DOMContentLoaded", function () {
    "use strict";

    if (typeof aspnetValidation === "undefined") {
        return;
    }

    var validation = new aspnetValidation.ValidationService();

    // watch:true so fields added after load are picked up. Nothing here does that today; it costs a
    // MutationObserver and removes a trap from whoever adds the first dynamic form.
    validation.bootstrap({ watch: true });
});

// Copy-to-clipboard for a value that exists to be pasted somewhere else - the print host address on
// a printer's page, which goes into a slicer's settings and nowhere else.
//
// The button is rendered unconditionally rather than revealed by this file. A control that appears
// only once script has run is a worse experience for everyone in exchange for tidiness towards
// nobody: the fallback below means the button does something useful even where the clipboard API is
// missing, which is not a hypothetical - navigator.clipboard is undefined outside a secure context,
// and a deployment reached over plain HTTP on anything but localhost is exactly that.
(function () {
    "use strict";

    var groups = document.querySelectorAll("[data-copy]");

    if (!groups.length) {
        return;
    }

    Array.prototype.forEach.call(groups, function (group) {
        var source = group.querySelector("[data-copy-source]");
        var button = group.querySelector("[data-copy-button]");

        if (!source || !button) {
            return;
        }

        // The status line lives outside the group so the layout does not move when it fills.
        var status = group.parentNode.querySelector("[data-copy-status]");
        var original = button.textContent;
        var revert;

        function say(message, copied) {
            button.textContent = copied ? "Copied" : original;

            if (status) {
                status.textContent = message;
            }

            window.clearTimeout(revert);

            revert = window.setTimeout(function () {
                button.textContent = original;

                if (status) {
                    status.textContent = "";
                }
            }, 2000);
        }

        // Selecting first is not decoration: it is the fallback. Where the clipboard API is absent
        // the text is left selected and ready for the keyboard, so the button still advances the
        // user rather than failing silently.
        function select() {
            source.focus();
            source.setSelectionRange(0, source.value.length);
        }

        button.addEventListener("click", function () {
            select();

            if (!navigator.clipboard) {
                say("Selected - press Ctrl+C or Cmd+C to copy.", false);

                return;
            }

            navigator.clipboard.writeText(source.value).then(
                function () {
                    say("Address copied to the clipboard.", true);
                },
                function () {
                    // Permission refused, or a browser that has the API and will not use it here.
                    say("Selected - press Ctrl+C or Cmd+C to copy.", false);
                });
        });

        // Clicking the field itself selects the whole address, which is what anyone reaching for it
        // is about to do by hand.
        source.addEventListener("focus", select);
    });
}());
