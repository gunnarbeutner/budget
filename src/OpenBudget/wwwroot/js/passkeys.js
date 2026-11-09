// WebAuthn glue for the login and account pages. The server (ASP.NET Core Identity's built-in
// passkey support) produces the options JSON and verifies the result; this file only converts
// between the JSON wire format and the browser's binary WebAuthn API. Prefers the native
// parse*OptionsFromJSON / toJSON helpers and falls back to manual base64url handling for browsers
// that don't have them yet (older iOS/Safari).
(function () {
  'use strict';

  function b64urlToBuffer(value) {
    var base64 = value.replace(/-/g, '+').replace(/_/g, '/');
    while (base64.length % 4) base64 += '=';
    var bin = atob(base64);
    var bytes = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes.buffer;
  }

  function bufferToB64url(buffer) {
    var bytes = new Uint8Array(buffer);
    var bin = '';
    for (var i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  function parseCreationOptions(json) {
    if (window.PublicKeyCredential && PublicKeyCredential.parseCreationOptionsFromJSON) {
      return PublicKeyCredential.parseCreationOptionsFromJSON(json);
    }
    var o = JSON.parse(JSON.stringify(json));
    o.challenge = b64urlToBuffer(o.challenge);
    o.user.id = b64urlToBuffer(o.user.id);
    (o.excludeCredentials || []).forEach(function (c) { c.id = b64urlToBuffer(c.id); });
    return o;
  }

  function parseRequestOptions(json) {
    if (window.PublicKeyCredential && PublicKeyCredential.parseRequestOptionsFromJSON) {
      return PublicKeyCredential.parseRequestOptionsFromJSON(json);
    }
    var o = JSON.parse(JSON.stringify(json));
    o.challenge = b64urlToBuffer(o.challenge);
    (o.allowCredentials || []).forEach(function (c) { c.id = b64urlToBuffer(c.id); });
    return o;
  }

  function credentialToJson(cred) {
    if (cred.toJSON) return JSON.stringify(cred);
    var r = cred.response;
    var response = { clientDataJSON: bufferToB64url(r.clientDataJSON) };
    if (r.attestationObject) {
      response.attestationObject = bufferToB64url(r.attestationObject);
      if (r.getTransports) response.transports = r.getTransports();
    } else {
      response.authenticatorData = bufferToB64url(r.authenticatorData);
      response.signature = bufferToB64url(r.signature);
      if (r.userHandle) response.userHandle = bufferToB64url(r.userHandle);
    }
    return JSON.stringify({
      id: cred.id,
      rawId: bufferToB64url(cred.rawId),
      type: cred.type,
      authenticatorAttachment: cred.authenticatorAttachment,
      response: response,
      clientExtensionResults: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {}
    });
  }

  function antiforgeryToken(form) {
    var input = form.querySelector('input[name="__RequestVerificationToken"]');
    return input ? input.value : '';
  }

  async function fetchOptions(form) {
    var response = await fetch(form.dataset.optionsUrl, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'RequestVerificationToken': antiforgeryToken(form) }
    });
    if (!response.ok) throw new Error('options ' + response.status);
    return response.json();
  }

  function showError(message) {
    var box = document.getElementById('passkey-error');
    if (!box) return;
    box.textContent = message;
    box.classList.remove('d-none');
  }

  function supported(form) {
    if (window.PublicKeyCredential && navigator.credentials) return true;
    showError(form.dataset.noPasskey);
    return false;
  }

  async function run(form, button, ceremony) {
    if (!supported(form)) return;
    button.disabled = true;
    try {
      var options = await fetchOptions(form);
      var credential = await ceremony(options);
      form.querySelector('#credentialJson').value = credentialToJson(credential);
      form.submit();
    } catch (e) {
      // NotAllowedError = the user dismissed the prompt; anything else is a real failure.
      if (!e || e.name !== 'NotAllowedError') showError(form.dataset.failed);
      button.disabled = false;
    }
  }

  window.OpenBudgetPasskeys = {
    wireLogin: function () {
      var form = document.getElementById('passkey-login-form');
      var button = document.getElementById('passkey-login');
      if (!form || !button) return;
      button.addEventListener('click', function () {
        run(form, button, function (options) {
          return navigator.credentials.get({ publicKey: parseRequestOptions(options) });
        });
      });
    },
    wireRegister: function () {
      var form = document.getElementById('passkey-register-form');
      var button = document.getElementById('passkey-register');
      if (!form || !button) return;
      button.addEventListener('click', function () {
        run(form, button, function (options) {
          return navigator.credentials.create({ publicKey: parseCreationOptions(options) });
        });
      });
    }
  };
})();
