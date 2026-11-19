# The browser sidecar of OpenBudget's web logins: selkies streams a shared X display, and the launcher starts one Chrome
# per web login on it (see docs/browser-sidecar.md). The base is pinned by digest; update it deliberately.
ARG BROWSER_IMAGE=ghcr.io/selkies-project/selkies/desktop@sha256:5327eae2cf2a67382e6c5708d32e2460ef687c0d6126c2f3b82e76442142b4a1
FROM ${BROWSER_IMAGE}
USER root
# socat exposes each Chrome's DevTools port; xdpyinfo (x11-utils) lets the service wait for the display; xdotool hides and
# raises windows.
RUN apt-get update && apt-get install -y --no-install-recommends socat x11-utils xdotool && rm -rf /var/lib/apt/lists/*
COPY chrome-policy.json /etc/opt/chrome/policies/managed/openbudget.json
COPY browser-launcher /usr/local/bin/browser-launcher
COPY run /etc/service/browser-launcher/run
RUN chmod 755 /usr/local/bin/browser-launcher /etc/service/browser-launcher/run && chown -R ubuntu:ubuntu /etc/service/browser-launcher
USER ubuntu
