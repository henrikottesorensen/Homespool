// Adding a passkey: ask the server for creation options, hand them to the browser's authenticator,
// and post what comes back. The registration half of what passkey-signin.js does for sign-in, with
// the same hand-written base64url in both directions - the two files share nothing on purpose, since
// a page loads one or the other and never both.
(function () {
    "use strict";

    var form = document.getElementById("passkey-register-form");
    var button = document.getElementById("passkey-add");
    var error = document.getElementById("passkey-error");

    if (!form || !button || !error) {
        return;
    }

    if (!window.PublicKeyCredential || !navigator.credentials || !navigator.credentials.create) {
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

    // The creation options as the server sends them, with the binary fields decoded: what
    // PublicKeyCredential.parseCreationOptionsFromJSON does in browsers that have it.
    function toCreationOptions(json) {
        var options = {
            rp: json.rp,
            user: {
                id: fromBase64Url(json.user.id),
                name: json.user.name,
                displayName: json.user.displayName
            },
            challenge: fromBase64Url(json.challenge),
            pubKeyCredParams: json.pubKeyCredParams,
            timeout: json.timeout,
            excludeCredentials: [],
            authenticatorSelection: json.authenticatorSelection,
            attestation: json.attestation
        };

        (json.excludeCredentials || []).forEach(function (descriptor) {
            options.excludeCredentials.push({
                type: descriptor.type,
                id: fromBase64Url(descriptor.id),
                transports: descriptor.transports || []
            });
        });

        return options;
    }

    // The credential as the server expects it: what credential.toJSON() returns.
    function toCredentialJson(credential) {
        var response = credential.response;

        return JSON.stringify({
            id: credential.id,
            rawId: toBase64Url(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment || null,
            clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {},
            response: {
                attestationObject: toBase64Url(response.attestationObject),
                clientDataJSON: toBase64Url(response.clientDataJSON),
                transports: response.getTransports ? response.getTransports() : []
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

        // Built from this form so the antiforgery field travels with the challenge request.
        var body = new FormData(form);

        fetch(form.dataset.passkeyOptions, { method: "POST", body: body, credentials: "same-origin" })
            .then(function (response) {
                if (response.status === 404) {
                    button.hidden = true;
                    throw new Error("withheld");
                }

                if (!response.ok) {
                    // A wrong password or a backoff answers with the sentence to show; anything
                    // else gets the generic one.
                    return response.json().then(function (body) {
                        throw new Error(body && body.message ? body.message : "");
                    }, function () {
                        throw new Error("");
                    });
                }

                return response.json();
            })
            .then(function (json) {
                return navigator.credentials.create({ publicKey: toCreationOptions(json) });
            })
            .then(function (credential) {
                if (!credential) {
                    throw new DOMException("No credential", "NotAllowedError");
                }

                form.elements.credential.value = toCredentialJson(credential);

                // The password was proved when the ceremony began; it has no business in the answer.
                if (form.elements["Input.Password"]) {
                    form.elements["Input.Password"].value = "";
                }

                // A native submit from here on: the server answers with a redirect or the page with
                // its message.
                HTMLFormElement.prototype.submit.call(form);
            })
            .catch(function (reason) {
                if (reason && reason.message === "withheld") {
                    return;
                }

                // NotAllowedError is cancelled or timed out; InvalidStateError is "this authenticator
                // already holds a passkey for this account", which the exclude list asks it to say. A
                // server refusal carries its own sentence.
                showError(reason && reason.message ? reason.message : form.dataset.passkeyCancelled);
            });
    });
})();
