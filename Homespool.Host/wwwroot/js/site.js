// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Picking a file, by drop or through the picker, uploads it as soon as it is chosen - the button
// stays for when this script never runs, but nobody has to reach for it otherwise. The objection
// window this skips would have cost a press on every intended upload to save an accidental one, and
// a file that lands in your own tree is one you can delete; the printer page's drop box answers that
// question separately because a drop straight onto a *printer* is not equally cheap.
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

    // The picker only ever hands back one file - there is no `multiple` on the input - so this is
    // the whole submit path for it. requestSubmit rather than submit: it runs the form's own
    // validation and fires the submit event, where submit() silently skips both. The fallback is for
    // a browser without it, where skipping validation is better than a choice that does nothing.
    input.addEventListener("change", function () {
        if (!input.form) {
            return;
        }

        if (input.form.requestSubmit) {
            input.form.requestSubmit();
        } else {
            input.form.submit();
        }
    });

    // A drop can carry more files than the picker ever could, so it gets its own path: post each
    // one to the same handler in turn, then reload once. Sequential rather than concurrent because
    // the handler holds one pending-conflict slot at a time - two uploads racing each other through
    // it would let the second's answer clobber the first's before anyone saw the question.
    var MAX_DROPPED_FILES = 16;

    function uploadDropped(files) {
        var status = document.createElement("p");

        status.className = "text-body-secondary small mb-0 mt-2";
        zone.appendChild(status);

        var index = 0;

        function next() {
            if (index >= files.length) {
                // Reloading rather than patching the table in place is what lets the redirect this
                // POST already produces - the file list, the flash message, a held conflict waiting
                // on Replace/Discard - render exactly as it would have for an ordinary form post.
                window.location.reload();

                return;
            }

            var file = files[index];

            index += 1;
            status.textContent = "Uploading " + index + " of " + files.length + ": " + file.name;

            // Built from the real form so the antiforgery field and the route values it already
            // carries - sort, printer, handler - travel with it unchanged; only the file differs
            // from what a native submit would have sent.
            var body = new FormData(input.form);

            body.set("file", file);

            fetch(input.form.action, { method: "POST", body: body, credentials: "same-origin" })
                .catch(function () {
                    // One file failing to reach the server is not a reason to abandon the rest.
                })
                .then(next, next);
        }

        next();
    }

    zone.addEventListener("drop", function (event) {
        if (!event.dataTransfer || event.dataTransfer.files.length === 0) {
            return;
        }

        var dropped = event.dataTransfer.files;

        // One file is unambiguous and goes straight through, same as the picker. More than one asks
        // first - a multi-file drop is easier to do by accident than a single one, and confirming
        // once is cheap next to discovering only afterwards that a whole folder went up.
        if (dropped.length === 1) {
            uploadDropped(dropped);

            return;
        }

        // Past the cap this refuses outright rather than offering a partial upload: silently
        // keeping sixteen out of however many were dropped is its own kind of surprise, and asking
        // "upload sixteen and skip the rest?" only invites picking a number nobody chose.
        if (dropped.length > MAX_DROPPED_FILES) {
            window.alert(
                "Only " + MAX_DROPPED_FILES + " files can be uploaded at once - " + dropped.length +
                " were dropped. Drop " + MAX_DROPPED_FILES + " or fewer.");

            return;
        }

        if (window.confirm("Upload these " + dropped.length + " files?")) {
            uploadDropped(dropped);
        }
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
