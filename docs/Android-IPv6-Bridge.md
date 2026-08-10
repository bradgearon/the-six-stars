# Android local IPv6 bridge preference

The Android companion now learns a stable, routable IPv6 address from a known-good local desktop connection and stores it as its preferred bridge address.

## Discovery

1. Android first connects using the currently available address.
2. The desktop examines the accepted control socket's local endpoint.
3. The desktop finds the network adapter that owns that endpoint.
4. It selects a non-loopback, non-multicast, non-link-local IPv6 address from that same adapter.
5. The bridge health response returns the address, adapter name, active local/remote endpoints, and an IPv6 bridge URL.
6. Android stores the address and promotes the control WebSocket and phone-audio socket to IPv6.

The desktop listeners for both the control bridge and phone audio are dual-stack. Existing IPv4 clients remain supported.

## Fallback behavior

IPv6 is attempted first when **Use saved IPv6 first for local connections** is enabled. If the connection fails, Android immediately returns to the configured desktop bridge URL. The failed IPv6 is suppressed for five minutes in the current app process to avoid a reconnect loop. It is tried again on a later app launch or after the setting is manually saved again.

Prepared speech and background initiative HTTP work also attempt IPv6 first and retry through the configured fallback address.

## Cat-face popup

Tap the cat face to open **Last Cat Console**. The popup now includes:

- the editable fallback desktop bridge URL;
- the saved preferred local IPv6 address;
- a checkbox controlling IPv6-first connection behavior;
- **Update IPv6 from Desktop**, which asks the active desktop socket to identify its transmitting adapter and stores the returned address;
- status lines showing the configured fallback, saved IPv6, IPv6 preference, and currently active bridge URL.

Link-local addresses beginning with `fe80:` are intentionally not stored. They require an interface scope identifier, and a Windows interface scope cannot safely be reused as an Android interface scope. A ULA (`fc00::/7`) or globally routable IPv6 address is required.
