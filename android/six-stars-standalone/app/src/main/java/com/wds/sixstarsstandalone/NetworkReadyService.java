package com.wds.sixstarsstandalone;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;

import java.net.ConnectException;
import java.net.NoRouteToHostException;
import java.net.SocketException;
import java.net.UnknownHostException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Event-driven Android network readiness helper used after device sleep/wake.
 *
 * This does not poll. It checks the active network once and, when Android has not finished restoring
 * a validated Internet route, temporarily waits on ConnectivityManager's default-network callback.
 * Provider clients can then retry a transient DNS/connect failure once against the restored route.
 */
final class NetworkReadyService {
    private final ConnectivityManager connectivity;
    private final RuntimeTrace trace;

    NetworkReadyService(Context context, RuntimeTrace trace) {
        connectivity = (ConnectivityManager) context.getApplicationContext().getSystemService(Context.CONNECTIVITY_SERVICE);
        this.trace = trace;
    }

    void onAppResumed() {
        if (trace != null) trace.log("network.app-resumed", describe());
    }

    boolean awaitValidatedInternet(long timeoutMs) throws InterruptedException {
        if (isValidatedInternetAvailable()) return true;
        if (connectivity == null) return false;

        CountDownLatch ready = new CountDownLatch(1);
        AtomicBoolean registered = new AtomicBoolean(false);
        ConnectivityManager.NetworkCallback callback = new ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(Network network) {
                if (hasValidatedInternet(network)) ready.countDown();
            }

            @Override public void onCapabilitiesChanged(Network network, NetworkCapabilities caps) {
                if (caps != null
                        && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                        && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) {
                    ready.countDown();
                }
            }
        };

        try {
            connectivity.registerDefaultNetworkCallback(callback);
            registered.set(true);
            // Close the race where the network became ready between the first check and callback registration.
            if (isValidatedInternetAvailable()) ready.countDown();
            boolean ok = ready.await(Math.max(250L, timeoutMs), TimeUnit.MILLISECONDS);
            if (trace != null) trace.log("network.await-complete", "validated=" + ok + " " + describe());
            return ok;
        } catch (SecurityException unavailable) {
            if (trace != null) trace.log("network.await-security-error", unavailable.toString());
            return false;
        } finally {
            if (registered.get()) {
                try { connectivity.unregisterNetworkCallback(callback); } catch (Exception ignored) { }
            }
        }
    }

    boolean isValidatedInternetAvailable() {
        if (connectivity == null) return true;
        try {
            Network active = connectivity.getActiveNetwork();
            return hasValidatedInternet(active);
        } catch (SecurityException ignored) {
            return true;
        }
    }

    private boolean hasValidatedInternet(Network network) {
        if (connectivity == null || network == null) return false;
        NetworkCapabilities caps = connectivity.getNetworkCapabilities(network);
        return caps != null
                && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
    }

    static boolean isTransientWakeNetworkFailure(Throwable error) {
        Throwable current = error;
        while (current != null) {
            if (current instanceof UnknownHostException
                    || current instanceof ConnectException
                    || current instanceof NoRouteToHostException
                    || current instanceof SocketException) return true;
            if (current.getCause() == current) break;
            current = current.getCause();
        }
        return false;
    }

    String describe() {
        if (connectivity == null) return "connectivity-manager=unavailable";
        try {
            Network active = connectivity.getActiveNetwork();
            if (active == null) return "activeNetwork=none";
            NetworkCapabilities caps = connectivity.getNetworkCapabilities(active);
            if (caps == null) return "activeNetwork=" + active + " capabilities=none";
            return "activeNetwork=" + active
                    + " internet=" + caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                    + " validated=" + caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
                    + " wifi=" + caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
                    + " cellular=" + caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR);
        } catch (SecurityException e) {
            return "network-state=permission-unavailable";
        }
    }
}
