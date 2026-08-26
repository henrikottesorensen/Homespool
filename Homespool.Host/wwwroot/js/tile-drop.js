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
// One file per drag. Dragging several onto a tile takes the first, which is what the Files zone
// does too - and here it matters more, because the dialog's third button starts a print, and
// "which of these five did I just start" is not a question a person should have.
//
// What a printer will accept is the server's business, not this file's. It answers the drop with a
// dialog that either asks the two questions or says the file is not one a printer would take.
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

    function ready(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
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

        // One step visible at a time. Each step carries its own buttons, so there is nothing to show
        // or hide beyond the step itself.
        function showStep(name) {
            content.querySelectorAll("[data-drop-step]").forEach(function (step) {
                step.classList.toggle("d-none", step.dataset.dropStep !== name);
            });

            // The bed-clear step carries a camera view, and it arrived with this dialog rather than
            // with the page - so camera.js has already done its binding pass and knows nothing about
            // it. Attaching on show also means nothing polls a camera nobody has opened.
            if (window.homespoolCameras) {
                window.homespoolCameras.attachWithin(content);
            }
        }

        content.addEventListener("click", function (event) {
            var goto = event.target.closest("[data-drop-goto]");

            if (goto) {
                showStep(goto.dataset.dropGoto);

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

        // DELEGATED FROM THE DOCUMENT, and this is the whole reason the first version did nothing.
        //
        // The tiles live inside a polled region: live-region.js replaces its innerHTML every ten
        // seconds. Listeners bound to the tiles themselves are bound to elements that stop existing
        // on the first refresh, so a drop worked for ten seconds after a page load and silently did
        // nothing ever after - no error, no request, nothing in the server log to find.
        //
        // _PrinterStatus.cshtml already records this lesson about the Set ready button surviving a
        // two-second refresh, because Bootstrap delegates from the document. So does this now.
        function tileFor(event) {
            return event.target instanceof Element ? event.target.closest("[data-drop-target]") : null;
        }

        function highlight(tile) {
            document.querySelectorAll(".printer-shortcut-drop").forEach(function (lit) {
                if (lit !== tile) {
                    lit.classList.remove("printer-shortcut-drop");
                }
            });

            if (tile) {
                tile.classList.add("printer-shortcut-drop");
            }
        }

        ["dragenter", "dragover"].forEach(function (name) {
            document.addEventListener(name, function (event) {
                var tile = tileFor(event);

                if (!tile) {
                    return;
                }

                // Only over a tile. preventDefault on every dragover would break the browser's own
                // handling everywhere else on the page; without it here, the browser navigates to
                // the dropped file and the page is gone.
                event.preventDefault();
                highlight(tile);
            });
        });

        ["dragleave", "dragend"].forEach(function (name) {
            document.addEventListener(name, function (event) {
                if (!tileFor(event)) {
                    highlight(null);
                }
            });
        });

        document.addEventListener("drop", function (event) {
            var tile = tileFor(event);

            highlight(null);

            if (!tile) {
                return;
            }

            event.preventDefault();

            if (!event.dataTransfer || !event.dataTransfer.files.length) {
                return;
            }

            // One file per drag, the same rule the Files zone follows. What a printer will take is
            // not decided here: the name goes to the server, which owns the list and answers with a
            // dialog that either asks the questions or says why not. An earlier cut kept its own
            // list of extensions, silently dropped anything else - so an STL did nothing at all -
            // and had drifted from the server's list in both directions.
            ask(tile.dataset.dropUuid, [event.dataTransfer.files[0]]);
        });
    });
})();
