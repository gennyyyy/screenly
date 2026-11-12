package com.screenly.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow

data class Host(val name: String, val host: String, val port: Int)

fun discoverHosts(context: Context): Flow<List<Host>> = callbackFlow {
    val nsd = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    val found = mutableListOf<Host>()
    val listener = object : NsdManager.DiscoveryListener {
        override fun onDiscoveryStarted(serviceType: String) {}
        override fun onServiceFound(info: NsdServiceInfo) {
            nsd.resolveService(info, object : NsdManager.ResolveListener {
                override fun onResolveFailed(si: NsdServiceInfo, code: Int) {}
                override fun onServiceResolved(si: NsdServiceInfo) {
                    found.add(Host(si.serviceName, si.host.hostAddress ?: "", si.port))
                    trySend(found.toList())
                }
            })
        }
        override fun onServiceLost(info: NsdServiceInfo) { found.removeAll { it.name == info.serviceName }; trySend(found.toList()) }
        override fun onDiscoveryStopped(serviceType: String) {}
        override fun onStartDiscoveryFailed(serviceType: String, code: Int) {}
        override fun onStopDiscoveryFailed(serviceType: String, code: Int) {}
    }
    nsd.discoverServices("_screenly._tcp.", NsdManager.PROTOCOL_DNS_SD, listener)
    awaitClose { nsd.stopServiceDiscovery(listener) }
}
