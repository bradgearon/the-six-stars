package com.wds.sixstarsmobile;

import org.json.JSONArray;
import org.json.JSONObject;

import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;

/** IPv4/IPv6 bridge address normalization and connection-candidate selection. */
final class BridgeAddressService {
    private BridgeAddressService() { }

    static String normalizeIpv6Address(String value) {
        if (value == null) return "";
        String candidate = value.trim();
        if (candidate.isEmpty()) return "";
        try {
            if (candidate.startsWith("http://") || candidate.startsWith("https://")
                    || candidate.startsWith("ws://") || candidate.startsWith("wss://")) {
                candidate = new URL(candidate.replaceFirst("^ws", "http")).getHost();
            }
        } catch (Exception ignored) { }
        if (candidate.startsWith("[") && candidate.endsWith("]")) {
            candidate = candidate.substring(1, candidate.length() - 1);
        }
        int scope = candidate.indexOf('%');
        if (scope >= 0) candidate = candidate.substring(0, scope);
        if (!candidate.contains(":")) return "";
        try {
            InetAddress address = InetAddress.getByName(candidate);
            if (!(address instanceof Inet6Address)) return "";
            if (address.isAnyLocalAddress() || address.isLoopbackAddress()
                    || address.isLinkLocalAddress() || address.isMulticastAddress()) return "";
            return address.getHostAddress().replaceAll("%.*$", "");
        } catch (Exception ignored) {
            return "";
        }
    }

    static String ipv6BridgeUrl(String ipv6Address, int port) {
        String normalized = normalizeIpv6Address(ipv6Address);
        if (normalized.isEmpty()) return "";
        return "http://[" + normalized + "]:" + normalizePort(port);
    }

    static int bridgePort(String baseUrl) {
        try {
            URL url = new URL(toHttpUrl(normalizeBaseUrl(baseUrl)));
            return url.getPort() > 0 ? url.getPort() : 8066;
        } catch (Exception ignored) {
            return 8066;
        }
    }

    static int audioPort(String baseUrl) {
        int port = bridgePort(baseUrl);
        return port >= 65535 ? port - 1 : port + 1;
    }

    static String hostFromBaseUrl(String baseUrl) {
        try {
            return new URL(toHttpUrl(normalizeBaseUrl(baseUrl))).getHost();
        } catch (Exception ignored) {
            return "";
        }
    }

    static boolean isIpv6Url(String baseUrl) {
        return normalizeIpv6Address(hostFromBaseUrl(baseUrl)).length() > 0;
    }

    static String discoveredIpv6(JSONObject health) {
        List<String> candidates = discoveredIpv6Candidates(health);
        return candidates.isEmpty() ? "" : candidates.get(0);
    }

    static List<String> discoveredIpv6Candidates(JSONObject health) {
        ArrayList<String> result = new ArrayList<>();
        if (health == null || !health.optBoolean("localConnection", true)) return result;
        JSONArray supplied = health.optJSONArray("preferredIpv6Addresses");
        if (supplied != null) {
            for (int i = 0; i < supplied.length(); i++) {
                String address = normalizeIpv6Address(supplied.optString(i, ""));
                if (!address.isEmpty() && !result.contains(address)) result.add(address);
            }
        }
        String preferred = normalizeIpv6Address(health.optString("preferredIpv6Address", ""));
        if (preferred.isEmpty()) preferred = normalizeIpv6Address(health.optString("preferredIpv6Host", ""));
        if (!preferred.isEmpty()) {
            result.remove(preferred);
            result.add(0, preferred);
        }
        return result;
    }

    static String normalizeBaseUrl(String value) {
        String normalized = value == null ? "" : value.trim();
        while (normalized.endsWith("/")) normalized = normalized.substring(0, normalized.length() - 1);
        if (normalized.isEmpty()) return MobileSettingsService.DEFAULT_BRIDGE_URL;
        if (!normalized.startsWith("http://") && !normalized.startsWith("https://")
                && !normalized.startsWith("ws://") && !normalized.startsWith("wss://")) {
            normalized = "http://" + normalized;
        }
        return normalized;
    }

    static List<String> connectionCandidates(MobileSettingsService settings) {
        ArrayList<String> result = new ArrayList<>();
        String fallback = normalizeBaseUrl(settings.bridgeUrl());
        if (settings.useIpv6Primary()) {
            String ipv6Url = ipv6BridgeUrl(settings.preferredIpv6Address(), bridgePort(fallback));
            if (!ipv6Url.isEmpty()) result.add(ipv6Url);
        }
        if (!result.contains(fallback)) result.add(fallback);
        return result;
    }

    private static String toHttpUrl(String value) {
        if (value.startsWith("ws://")) return "http://" + value.substring(5);
        if (value.startsWith("wss://")) return "https://" + value.substring(6);
        return value;
    }

    private static int normalizePort(int port) {
        return port > 0 && port <= 65535 ? port : 8066;
    }
}
