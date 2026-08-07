package com.wds.sixstarsmobile;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;

import java.net.HttpURLConnection;
import java.net.Inet4Address;
import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.net.URLConnection;
import java.util.Collections;
import java.util.List;

import javax.net.SocketFactory;

/**
 * Routes local desktop bridge traffic through Android's Wi-Fi network instead of allowing the
 * platform to choose cellular data. This matters when mobile data remains the default network while
 * Wi-Fi is connected only for LAN access: an fd00::/8 or 192.168.x.x bridge address is otherwise
 * attempted from the carrier interface and can never reach the desktop.
 */
final class LocalNetworkBindingService {
    private final ConnectivityManager connectivityManager;

    LocalNetworkBindingService(Context context) {
        connectivityManager = (ConnectivityManager) context.getApplicationContext()
                .getSystemService(Context.CONNECTIVITY_SERVICE);
    }

    SocketFactory socketFactoryForHost(String host) {
        Network network = preferredWifiNetwork(host);
        return network == null ? null : network.getSocketFactory();
    }

    HttpURLConnection openHttpConnection(URL url) throws Exception {
        Network network = preferredWifiNetwork(url == null ? "" : url.getHost());
        URLConnection connection = network == null ? url.openConnection() : network.openConnection(url);
        if (!(connection instanceof HttpURLConnection)) {
            throw new IllegalStateException("Expected HTTP connection for " + url);
        }
        return (HttpURLConnection) connection;
    }

    Socket connectSocket(String host, int port, int timeoutMs) throws Exception {
        SocketFactory factory = socketFactoryForHost(host);
        Socket socket = factory == null ? new Socket() : factory.createSocket();
        socket.connect(new InetSocketAddress(stripBrackets(host), port), Math.max(1000, timeoutMs));
        return socket;
    }

    String choosePreferredIpv6(List<String> candidates) {
        if (candidates == null) candidates = Collections.emptyList();
        String best = "";
        int bestScore = Integer.MIN_VALUE;
        for (String candidate : candidates) {
            String normalized = BridgeAddressService.normalizeIpv6Address(candidate);
            if (normalized.isEmpty()) continue;
            int score = scoreCandidate(normalized);
            if (score > bestScore) {
                best = normalized;
                bestScore = score;
            }
        }
        return best;
    }

    String routeDescription(String host) {
        Network network = preferredWifiNetwork(host);
        if (network == null) return "default Android network (no Wi-Fi LAN route found)";
        LinkProperties properties = connectivityManager == null ? null : connectivityManager.getLinkProperties(network);
        String interfaceName = properties == null ? "Wi-Fi" : properties.getInterfaceName();
        return "Wi-Fi network " + network + (interfaceName == null || interfaceName.isEmpty() ? "" : " (" + interfaceName + ")");
    }

    boolean hasWifiNetwork() {
        if (connectivityManager == null) return false;
        for (Network network : connectivityManager.getAllNetworks()) {
            NetworkCapabilities capabilities = connectivityManager.getNetworkCapabilities(network);
            if (isUsableWifi(capabilities)) return true;
        }
        return false;
    }

    private int scoreCandidate(String candidate) {
        InetAddress address = parseAddress(candidate);
        if (!(address instanceof Inet6Address)) return Integer.MIN_VALUE;
        Network network = preferredWifiNetwork(candidate);
        int score = network == null ? 0 : 100;
        LinkProperties properties = network == null || connectivityManager == null
                ? null : connectivityManager.getLinkProperties(network);
        if (properties != null) {
            for (LinkAddress local : properties.getLinkAddresses()) {
                InetAddress localAddress = local.getAddress();
                if (!(localAddress instanceof Inet6Address)) continue;
                if (sharesPrefix(localAddress, address, local.getPrefixLength())) score += 1000;
                else score += 10;
            }
        }
        // Prefer a globally routable candidate when neither address is known to share the Wi-Fi
        // prefix. A ULA remains preferred whenever the phone actually has the matching local prefix.
        if (!isUniqueLocalIpv6(address)) score += 40;
        else score += 20;
        return score;
    }

    private Network preferredWifiNetwork(String host) {
        if (connectivityManager == null) return null;
        InetAddress target = parseAddress(host);
        Network active = connectivityManager.getActiveNetwork();
        Network best = null;
        int bestScore = Integer.MIN_VALUE;
        for (Network network : connectivityManager.getAllNetworks()) {
            NetworkCapabilities capabilities = connectivityManager.getNetworkCapabilities(network);
            if (!isUsableWifi(capabilities)) continue;
            int score = network.equals(active) ? 20 : 0;
            LinkProperties properties = connectivityManager.getLinkProperties(network);
            if (properties != null && target != null) {
                boolean matchingFamily = false;
                for (LinkAddress local : properties.getLinkAddresses()) {
                    InetAddress localAddress = local.getAddress();
                    if (localAddress == null || localAddress.getClass() != target.getClass()) continue;
                    matchingFamily = true;
                    score += 30;
                    if (sharesPrefix(localAddress, target, local.getPrefixLength())) score += 1000;
                }
                if (!matchingFamily) score -= 50;
            }
            if (score > bestScore) {
                best = network;
                bestScore = score;
            }
        }
        return best;
    }

    private static boolean isUsableWifi(NetworkCapabilities capabilities) {
        return capabilities != null
                && capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
                && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
    }

    private static InetAddress parseAddress(String host) {
        String candidate = stripBrackets(host);
        if (candidate.isEmpty()) return null;
        int scope = candidate.indexOf('%');
        if (scope >= 0) candidate = candidate.substring(0, scope);
        try {
            return InetAddress.getByName(candidate);
        } catch (Exception ignored) {
            return null;
        }
    }

    private static String stripBrackets(String value) {
        String result = value == null ? "" : value.trim();
        if (result.startsWith("[") && result.endsWith("]")) {
            result = result.substring(1, result.length() - 1);
        }
        return result;
    }

    private static boolean isUniqueLocalIpv6(InetAddress address) {
        if (!(address instanceof Inet6Address)) return false;
        byte[] bytes = address.getAddress();
        return bytes.length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static boolean sharesPrefix(InetAddress left, InetAddress right, int prefixLength) {
        if (left == null || right == null || left.getClass() != right.getClass()) return false;
        byte[] a = left.getAddress();
        byte[] b = right.getAddress();
        if (prefixLength <= 0 || prefixLength > a.length * 8) return false;
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        for (int i = 0; i < fullBytes; i++) if (a[i] != b[i]) return false;
        if (remainingBits == 0) return true;
        int mask = 0xFF << (8 - remainingBits);
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}
