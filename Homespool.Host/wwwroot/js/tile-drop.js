// Dropping files onto a printer tile.
//
// The shape, and why it is not the Files page's shape. There, a drop fills a file input and submits
// the form - the intent is unambiguous, so the drop is the commit (notes/printer-page.md §6f). Here
// a drop onto a *printer* could mean three different things, and one of them starts a print without
// anybody looking at the machine. So the drop opens a dialog and the dialog commits.
//
// Two questions, in this order: name clashes first, then what to do. Clashes are asked before a byte
// moves, which is what makes "keep mine" free to answer - nothing has been written, so nothing has to
// be undone. The server answers which names clash, and renders both steps; this file only shows and
// hides them.
//
// One file per drag. Dragging several onto a tile takes the first print file among them, which is
// what the Files zone does too - and here it matters more, because the dialog's third button starts
// a print, and "which of these five did I just start" is not a question a person should have.
//
// The file itself never goes through the dialog. It is held in the FileList the drop carried and
// handed to a real <form> at the end, so the upload is an ordinary multipart POST with the
// antiforgery token the form already has - no fetch of file bytes, no progress bar to invent, and a
// browser that hates our JavaScript still gets a page it can read afterwards.
//
// Nothing here is required for the page to work: without script a tile is a link to the printer,
// which is what it was before any of this.
(function () {
    'use strict';

    // Refused in the browser, before anything is sent. The server checks again - these are for the
    // person's benefit, not for safety.
    var EXTENSIONS = [".gcode", ".bgcode", ".gco", ".g"];

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    function accepted(file) {
        var name = file.name.toLowerCase();

        return EXTENSIONS.some(function (extension) {
            return name.endsWith(extension);
        });
    }

    ready(function () {
        var form = document.querySelector("[data-drop-form]");
        var dialog = document.querySelector("[data-drop-dialog]");

        if (!form || !dialog || !window.bootstrap) {
            return;
        }

        var content = dialog.querySelector("[data-drop-dialog-content]");
        var modal = new window.bootstrap.Modal(dialog);

        // The drop that is currently being asked about. Held here rather than on the form, because
        // the form only learns about it once every question has an answer.
        var pending = null;

        function token() {
            var input = form.querySelector('input[name="__RequestVerificationToken"]');

            return input ? input.value : null;
        }

        // Files land in the form's own input rather than being posted by hand, so the browser builds
        // the multipart body. DataTransfer is the only way to write a FileList.
        function submit(action) {
            var transfer = new DataTransfer();

            Array.prototype.forEach.call(pending.files, function (file) {
                transfer.items.add(file);
            });

            form.querySelector("[data-drop-form-files]").files = transfer.files;
            form.querySelector("[data-drop-form-uuid]").value = pending.uuid;
            form.querySelector("[data-drop-form-action]").value = action;

            // One hidden input per file the reader chose to replace. Absent means keep, which is the
            // default the dialog renders and the safe answer.
            var replace = form.querySelector("[data-drop-form-replace]");
            replace.innerHTML = "";

            content.querySelectorAll('input[type="radio"]:checked').forEach(function (radio) {
                if (radio.value !== "replace") {
                    return;
                }

                var input = document.createElement("input");
                input.type = "hidden";
                input.name = "replace";
                input.value = radio.name.substring("clash:".length);
                replace.appendChild(input);
            });

            modal.hide();
            form.submit();
        }

        content.addEventListener("click", function (event) {
            var advance = event.target.closest("[data-drop-continue]");

            if (advance) {
                content.querySelector('[data-drop-step="clash"]').classList.add("d-none");
                content.querySelector('[data-drop-step="action"]').classList.remove("d-none");
                advance.classList.add("d-none");
                content.querySelector("[data-drop-actions]").classList.remove("d-none");

                return;
            }

            var chosen = event.target.closest("[data-drop-action]");

            if (chosen) {
                submit(chosen.dataset.dropAction);
            }
        });

        function ask(uuid, files) {
            var body = new FormData();
            body.append("uuid", uuid);

            Array.prototype.forEach.call(files, function (file) {
                body.append("names", file.name);
            });

            var verification = token();

            if (verification) {
                body.append("__RequestVerificationToken", verification);
            }

            fetch(window.location.pathname + "?handler=Conflicts", {
                method: "POST",
                body: body,
                headers: { "X-Requested-With": "XMLHttpRequest" },
                credentials: "same-origin"
            }).then(function (response) {
                // A redirect here is the login page, the same trap live-region.js records: without
                // this the dialog would cheerfully render a sign-in form inside itself.
                if (!response.ok || response.redirected) {
                    return null;
                }

                return response.text();
            }).then(function (html) {
                if (html === null) {
                    return;
                }

                pending = { uuid: uuid, files: files };
                content.innerHTML = html;
                modal.show();
            }).catch(function () {
                pending = null;
            });
        }

        document.querySelectorAll("[data-drop-target]").forEach(function (tile) {
            ["dragenter", "dragover"].forEach(function (name) {
                tile.addEventListener(name, function (event) {
                    // Both, or the browser navigates to the dropped file and the page is gone.
                    event.preventDefault();
                    event.stopPropagation();
                    tile.classList.add("printer-shortcut-drop");
                });
            });

            ["dragleave", "dragend", "drop"].forEach(function (name) {
                tile.addEventListener(name, function () {
                    tile.classList.remove("printer-shortcut-drop");
                });
            });

            tile.addEventListener("drop", function (event) {
                event.preventDefault();
                event.stopPropagation();

                if (!event.dataTransfer || !event.dataTransfer.files.length) {
                    return;
                }

                // One file per drag, the same rule the Files zone follows. A drag carrying several
                // takes the first that is a print file rather than refusing the lot: the drop was
                // still aimed at this printer, and the reader can see in the dialog which one it got.
                var file = Array.prototype.find.call(event.dataTransfer.files, accepted);

                if (!file) {
                    return;
                }

                ask(tile.dataset.dropUuid, [file]);
            });
        });
    });
})();
