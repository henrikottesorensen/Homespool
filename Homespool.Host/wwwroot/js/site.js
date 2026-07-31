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
