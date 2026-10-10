// The notifications settings page: register the push worker, ask the browser for permission and a
// subscription, and hand what it returns to the page's own form. The server stores it; nothing here
// talks to it except by submitting that form.
//
// A subscription this browser already holds is reused only if it was made with the deployment's key
// and is on this account's list, and replaced otherwise: one made with another key can never be
// delivered to, and the server refuses an endpoint another account has rather than move it, so a
// browser changing hands needs an endpoint of its own.
(function () {
    "use strict";

    const root = document.getElementById("notifications");
    const form = document.getElementById("notifications-subscribe-form");
    const state = document.getElementById("notifications-state");

    // Absent until the person has given a recent proof: then the page only reports on this browser.
    const button = document.getElementById("notifications-enable");
    const confirmFirst = document.getElementById("notifications-confirm-first");

    if (!root || !form || !state) {
        return;
    }

    function show(message) {
        state.textContent = message;
        state.hidden = false;
    }

    // For a browser that cannot be subscribed at all, where proving yourself first would lead nowhere.
    function cannot(message) {
        show(message);

        if (confirmFirst) {
            confirmFirst.hidden = true;
        }
    }

    function offerButton() {
        if (button) {
            button.hidden = false;
        }
    }

    // In this order because each answer makes the next question meaningless: without a secure
    // context there is no service worker to ask about, and without one there is no permission.
    if (!window.isSecureContext) {
        cannot(root.dataset.textInsecure);
        return;
    }

    if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
        cannot(root.dataset.textUnsupported);
        return;
    }

    if (Notification.permission === "denied") {
        cannot(root.dataset.textBlocked);
        return;
    }

    function fromBase64Url(text) {
        const base64 = text.replace(/-/g, "+").replace(/_/g, "/");
        const padded = base64 + "===".slice((base64.length + 3) % 4);
        const binary = window.atob(padded);
        const bytes = new Uint8Array(binary.length);

        for (let i = 0; i < binary.length; i += 1) {
            bytes[i] = binary.charCodeAt(i);
        }

        return bytes;
    }

    function toBase64Url(buffer) {
        const bytes = new Uint8Array(buffer);
        let binary = "";

        for (let i = 0; i < bytes.length; i += 1) {
            binary += String.fromCharCode(bytes[i]);
        }

        return window.btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    const serverKey = fromBase64Url(root.dataset.applicationServerKey);

    function madeWithServerKey(subscription) {
        const key = subscription.options && subscription.options.applicationServerKey;

        if (!key) {
            return false;
        }

        const bytes = new Uint8Array(key);

        return bytes.length === serverKey.length && bytes.every(function (value, index) {
            return value === serverKey[index];
        });
    }

    // What the server renders on each row instead of the endpoint itself: the start of its SHA-256.
    function endpointHash(endpoint) {
        return window.crypto.subtle.digest("SHA-256", new TextEncoder().encode(endpoint))
            .then(function (digest) {
                return toBase64Url(digest).slice(0, 22);
            });
    }

    function rowFor(hash) {
        return document.querySelector('tr[data-endpoint-hash="' + hash + '"]');
    }

    // This browser's row, if it has one: marked, and its Remove made to unsubscribe here as well, so
    // the browser stops asking its push service for messages nobody will send.
    function markThisBrowser(subscription, row) {
        const badge = row.querySelector("[data-this-browser]");
        const remove = row.querySelector("form[data-remove-destination]");

        if (badge) {
            badge.hidden = false;
        }

        if (remove) {
            remove.addEventListener("submit", function (event) {
                event.preventDefault();

                subscription.unsubscribe().catch(function () {
                    // The server's row is what matters; a browser that will not let go is asked again
                    // by nobody.
                }).then(function () {
                    HTMLFormElement.prototype.submit.call(remove);
                });
            });
        }
    }

    function reusable(subscription) {
        if (!madeWithServerKey(subscription)) {
            return Promise.resolve(false);
        }

        return endpointHash(subscription.endpoint).then(function (hash) {
            return rowFor(hash) !== null;
        });
    }

    function submitSubscription(subscription) {
        const json = subscription.toJSON();

        form.elements.endpoint.value = json.endpoint;
        form.elements.p256dh.value = json.keys.p256dh;
        form.elements.auth.value = json.keys.auth;

        HTMLFormElement.prototype.submit.call(form);
    }

    // The registration once its worker is active, which subscribing needs. Not
    // navigator.serviceWorker.ready: that waits for a worker controlling this page, and this one is
    // scoped to its own directory and controls no page at all, so it would wait for ever.
    function whenActive(registration) {
        if (registration.active) {
            return Promise.resolve(registration);
        }

        const worker = registration.installing || registration.waiting;

        if (!worker) {
            return Promise.reject(new Error("no worker"));
        }

        if (worker.state === "activated") {
            return Promise.resolve(registration);
        }

        return new Promise(function (resolve, reject) {
            worker.addEventListener("statechange", function () {
                if (worker.state === "activated") {
                    resolve(registration);
                } else if (worker.state === "redundant") {
                    reject(new Error("redundant"));
                }
            });
        });
    }

    const ready = navigator.serviceWorker.register(root.dataset.worker).then(whenActive);

    ready
        .then(function (registration) {
            return registration.pushManager.getSubscription();
        })
        .then(function (subscription) {
            if (!subscription || !madeWithServerKey(subscription) || Notification.permission !== "granted") {
                offerButton();
                return null;
            }

            return endpointHash(subscription.endpoint).then(function (hash) {
                const row = rowFor(hash);

                if (row) {
                    markThisBrowser(subscription, row);
                    show(root.dataset.textEnabledHere);

                    if (confirmFirst) {
                        confirmFirst.hidden = true;
                    }
                } else {
                    // Subscribed here, but not known to the server under this account - removed from
                    // another browser, or subscribed while somebody else was signed in. Enabling
                    // replaces it.
                    offerButton();
                }

                return null;
            });
        })
        .catch(function () {
            show(root.dataset.textFailed);
        });

    if (!button) {
        return;
    }

    form.addEventListener("submit", function (event) {
        event.preventDefault();
        button.disabled = true;

        Notification.requestPermission()
            .then(function (permission) {
                if (permission !== "granted") {
                    throw new Error(permission === "denied" ? "blocked" : "dismissed");
                }

                return ready;
            })
            .then(function (registration) {
                return registration.pushManager.getSubscription().then(function (existing) {
                    if (!existing) {
                        return null;
                    }

                    return reusable(existing).then(function (reuse) {
                        if (reuse) {
                            return existing;
                        }

                        return existing.unsubscribe().then(function () {
                            return null;
                        });
                    });
                }).then(function (existing) {
                    return existing || registration.pushManager.subscribe({
                        userVisibleOnly: true,
                        applicationServerKey: serverKey,
                    });
                });
            })
            .then(submitSubscription)
            .catch(function (reason) {
                button.disabled = false;

                if (reason && reason.message === "blocked") {
                    button.hidden = true;
                    cannot(root.dataset.textBlocked);
                } else if (!reason || reason.message !== "dismissed") {
                    show(root.dataset.textFailed);
                }
            });
    });
})();
