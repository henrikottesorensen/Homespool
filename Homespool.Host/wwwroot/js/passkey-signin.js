// Signing in with a passkey: ask the server for a challenge, hand it to the browser's authenticator,
// and post what comes back. Hand-written over navigator.credentials, because the server already
// speaks WebAuthn's JSON and the only work left is base64url in both directions.
//
// The button is hidden until this script has confirmed the browser can run a ceremony at all, since
// there is no no-script path for WebAuthn to fall back to. The form it lives in carries the
// antiforgery token, so both the challenge request and the answer travel with it.
(function () {
    "use strict";

    var form = document.getElementById("passkey-form");
    var button = document.getElementById("passkey-signin");
    var error = document.getElementById("passkey-error");

    if (!form || !button || !error) {
        return;
    }

    if (!window.PublicKeyCredential || !navigator.credentials || !navigator.credentials.get) {
        return;
    }

    button.hidden = false;

    function fromBase64Url(text) {
        var base64 = text.replace(/-/g, "+").replace(/_/g, "/");
        var padded = base64 + "===".slice((base64.length + 3) % 4);
        var binary = window.atob(padded);
        var bytes = new Uint8Array(binary.length);

        for (var i = 0; i < binary.length; i += 1) {
            bytes[i] = binary.charCodeAt(i);
        }

        return bytes.buffer;
    }

    function toBase64Url(buffer) {
        var bytes = new Uint8Array(buffer);
        var binary = "";

        for (var i = 0; i < bytes.length; i += 1) {
            binary += String.fromCharCode(bytes[i]);
        }

        return window.btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    // The request options as the server sends them, with the two binary fields decoded. What
    // PublicKeyCredential.parseRequestOptionsFromJSON does in browsers that have it, done by hand so
    // that the ones that do not are not left out.
    function toRequestOptions(json) {
        var options = {
            challenge: fromBase64Url(json.challenge),
            rpId: json.rpId,
            timeout: json.timeout,
            userVerification: json.userVerification,
            allowCredentials: []
        };

        (json.allowCredentials || []).forEach(function (descriptor) {
            options.allowCredentials.push({
                type: descriptor.type,
                id: fromBase64Url(descriptor.id),
                transports: descriptor.transports || []
            });
        });

        return options;
    }

    // The credential as the server expects it: what credential.toJSON() returns, done by hand for the
    // same reason as above.
    function toCredentialJson(credential) {
        var response = credential.response;

        return JSON.stringify({
            id: credential.id,
            rawId: toBase64Url(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment || null,
            clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {},
            response: {
                authenticatorData: toBase64Url(response.authenticatorData),
                clientDataJSON: toBase64Url(response.clientDataJSON),
                signature: toBase64Url(response.signature),
                userHandle: response.userHandle ? toBase64Url(response.userHandle) : null
            }
        });
    }

    function showError(message) {
        error.textContent = message;
        error.hidden = false;
        button.disabled = false;
    }

    form.addEventListener("submit", function (event) {
        event.preventDefault();

        error.hidden = true;
        button.disabled = true;

        // The remember-me choice is on the password form, and it means the same thing here.
        var remember = document.querySelector("#account input[type=checkbox]");
        form.elements.rememberMe.value = remember && remember.checked ? "true" : "false";

        // Built from this form so the antiforgery field travels with the challenge request.
        var body = new FormData(form);

        fetch(form.dataset.passkeyOptions, { method: "POST", body: body, credentials: "same-origin" })
            .then(function (response) {
                if (response.status === 404) {
                    // Withheld: no relying-party id, or a host it does not cover. Nothing to offer.
                    button.hidden = true;
                    throw new Error("withheld");
                }

                if (!response.ok) {
                    throw new Error("challenge " + response.status);
                }

                return response.json();
            })
            .then(function (json) {
                return navigator.credentials.get({ publicKey: toRequestOptions(json) });
            })
            .then(function (credential) {
                if (!credential) {
                    throw new DOMException("No credential", "NotAllowedError");
                }

                form.elements.credential.value = toCredentialJson(credential);

                // A native submit from here on: the server answers with a redirect or the page with
                // its message, exactly as the password form's post does.
                HTMLFormElement.prototype.submit.call(form);
            })
            .catch(function (reason) {
                if (reason && reason.message === "withheld") {
                    return;
                }

                // NotAllowedError is the browser's word for "cancelled, timed out, or no passkey
                // here"; the message says so and leaves the password form as it was.
                showError(form.dataset.passkeyCancelled);
            });
    });
})();
