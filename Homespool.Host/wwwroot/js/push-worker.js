// The service worker that shows Homespool's notifications. It handles a push and a click on what the
// push showed, and nothing else - no fetch handler, so it never answers for a page and never caches
// one. A signed-in page served from a cache is a page from before somebody signed out.
//
// Registered with its default scope, this script's own directory, which holds no pages at all. Push
// does not care about scope; a worker that controls nothing cannot get in the way of anything.
(function () {
    "use strict";

    const home = new URL("/", self.location.origin).href;

    // Only somewhere on this deployment. The payload is ours and encrypted to this browser, but a
    // notification is not the place to find out otherwise.
    function onThisSite(url) {
        if (typeof url !== "string") {
            return home;
        }

        try {
            const resolved = new URL(url, self.location.origin);

            return resolved.origin === self.location.origin ? resolved.href : home;
        } catch {
            return home;
        }
    }

    self.addEventListener("push", function (event) {
        let message;

        try {
            message = event.data ? event.data.json() : {};
        } catch {
            message = {};
        }

        const title = typeof message.title === "string" && message.title.length > 0 ? message.title : "Homespool";
        const options = {
            body: typeof message.body === "string" ? message.body : "",
            icon: "/img/apple-touch-icon.png",
            data: { url: onThisSite(message.url) },
        };

        if (typeof message.tag === "string" && message.tag.length > 0) {
            options.tag = message.tag;
        }

        // Always shown: a push that shows nothing is one the browser holds against the site, and may
        // end the subscription for.
        event.waitUntil(self.registration.showNotification(title, options));
    });

    self.addEventListener("notificationclick", function (event) {
        const url = event.notification.data && event.notification.data.url ? event.notification.data.url : home;

        event.notification.close();

        event.waitUntil(self.clients.matchAll({ type: "window", includeUncontrolled: true })
            .then(function (windows) {
                const open = windows.find(function (client) {
                    return client.url === url && "focus" in client;
                });

                return open ? open.focus() : self.clients.openWindow(url);
            }));
    });
})();
