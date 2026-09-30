// Arranges the pictures at the top of the printer page: a camera on the left, and on the right the
// slicer's preview of what is printing - or, with nothing printing, a second camera. A printer with no
// camera shows the preview alone, and the row is hidden while there is nothing in it.
//
// Nothing is ever moved. Every camera's figure is already in the page, and this only decides which
// are on show: a live view plays in its own element, and a figure that stays where it is keeps it.
// The two on show are put in order with a class rather than by moving them for the same reason.
//
// A camera nobody can see is parked - an attribute camera.js reads when it starts, and an event
// after that - so it stops asking for pictures. The server captures only while somebody asks, so a
// hidden camera left polling would keep one busy for nobody.
//
// The choice is remembered per browser and per printer, by camera rather than by position, so a
// camera added or removed later does not shift what somebody chose onto a different one.
(function () {
    "use strict";

    const STORAGE_PREFIX = "homespool.camera-deck.";

    function ready(fn) {
        if (document.readyState !== "loading") {
            fn();
        } else {
            document.addEventListener("DOMContentLoaded", fn);
        }
    }

    function attach(deck) {
        const items = Array.prototype.slice.call(deck.querySelectorAll("[data-deck-camera]"));
        const thumbnail = deck.querySelector("[data-deck-thumbnail]");
        const slot = thumbnail ? thumbnail.querySelector("[data-deck-thumbnail-slot]") : null;
        const count = items.length;
        const storageKey = STORAGE_PREFIX + deck.dataset.cameraDeck;

        // Positions in items. -1 on the right means nothing there: one camera, and nothing printing.
        let left = 0;
        let right = count > 1 ? 1 : -1;

        function showingPrint() {
            return !!slot && !!slot.querySelector("[data-print-thumbnail]");
        }

        // The camera after `from` going `direction`, stepping over `skip` - the one on show beside it.
        function next(from, direction, skip) {
            let candidate = from;

            do {
                candidate = (candidate + direction + count) % count;
            } while (candidate === skip && candidate !== from);

            return candidate;
        }

        function positionOf(camera) {
            for (let index = 0; index < count; index++) {
                if (items[index].dataset.deckCamera === camera) {
                    return index;
                }
            }

            return -1;
        }

        // Storage can be refused outright - a private window, blocked site data - and then the page
        // simply starts from the first camera each time.
        function restore() {
            try {
                const saved = JSON.parse(window.localStorage.getItem(storageKey) || "null");

                if (saved) {
                    const savedLeft = positionOf(saved.left);
                    const savedRight = positionOf(saved.right);

                    if (savedLeft >= 0) {
                        left = savedLeft;
                    }

                    if (savedRight >= 0) {
                        right = savedRight;
                    }
                }
            } catch {
                // Nothing remembered.
            }
        }

        function save() {
            try {
                window.localStorage.setItem(storageKey, JSON.stringify({
                    left: items[left].dataset.deckCamera,
                    right: right >= 0 ? items[right].dataset.deckCamera : null,
                }));
            } catch {
                // Not remembered, which costs a click next time and nothing else.
            }
        }

        function place(item, onShow) {
            const view = item.querySelector("[data-camera-frame]");
            const wasOnShow = !item.hidden;

            item.hidden = !onShow;

            if (!view) {
                return;
            }

            view.toggleAttribute("data-camera-parked", !onShow);

            if (wasOnShow !== onShow) {
                view.dispatchEvent(new CustomEvent(onShow ? "camera-unparked" : "camera-parked"));
            }
        }

        function layout() {
            const printing = showingPrint();

            // A camera chosen for the left while the preview had the right can be the one the right
            // had before; when the preview goes, the right moves on rather than show it twice.
            if (!printing && count > 1 && (right < 0 || right === left)) {
                right = next(left, 1, left);
            }

            for (let index = 0; index < count; index++) {
                const item = items[index];

                place(item, index === left || (!printing && index === right));

                // order-first rather than moving the element: the left camera can come later in the
                // page than the right one.
                item.classList.toggle("order-first", index === left);
            }

            if (thumbnail) {
                thumbnail.hidden = !printing;
            }

            deck.hidden = count === 0 && !printing;

            const places = printing ? 1 : Math.min(count, 2);

            deck.classList.toggle("camera-deck-switching", count > places);
        }

        deck.addEventListener("click", function (event) {
            const button = event.target.closest("[data-deck-step]");

            if (!button || !deck.contains(button)) {
                return;
            }

            const position = items.indexOf(button.closest("[data-deck-camera]"));
            const direction = parseInt(button.dataset.deckStep, 10);
            let shown;

            if (position === left) {
                left = next(left, direction, showingPrint() ? -1 : right);
                shown = left;
            } else if (position === right) {
                right = next(right, direction, left);
                shown = right;
            } else {
                return;
            }

            save();
            layout();

            // The button pressed has just been hidden with its camera. Focus goes to the same button
            // on the camera that replaced it, so stepping on with the keyboard needs no hunting.
            const same = items[shown].querySelector('[data-deck-step="' + button.dataset.deckStep + '"]');

            if (same) {
                same.focus();
            }
        });

        // The status card's poll carries the preview for whatever is printing now. Replaced only
        // when it names a different print, which is what a print starting or ending looks like.
        document.addEventListener("live-region-updated", function (event) {
            const template = event.target.querySelector("template[data-print-thumbnail-template]");

            if (!template || !slot) {
                return;
            }

            const incoming = template.content.querySelector("[data-print-thumbnail]");
            const current = slot.querySelector("[data-print-thumbnail]");
            const incomingPrint = incoming ? incoming.dataset.printThumbnail : "";
            const currentPrint = current ? current.dataset.printThumbnail : "";

            if (incomingPrint === currentPrint) {
                return;
            }

            slot.replaceChildren(template.content.cloneNode(true));
            layout();
        });

        // A preview that will not load gives its place back to a camera rather than sit as a broken
        // image. Captured, because an image's error does not bubble.
        if (slot) {
            slot.addEventListener("error", function (event) {
                if (event.target.tagName === "IMG") {
                    slot.replaceChildren();
                    layout();
                }
            }, true);
        }

        restore();
        layout();
    }

    ready(function () {
        const decks = document.querySelectorAll("[data-camera-deck]");

        for (let index = 0; index < decks.length; index++) {
            attach(decks[index]);
        }
    });
})();
