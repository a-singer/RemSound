import Foundation
import Network

class NetworkService: ObservableObject {
    private var listener: NWListener?
    private var discoveryTimer: Timer?
    private var heartbeatTimer: Timer?
    private var targetEndpoint: NWEndpoint?
    
    private var activeConnections: [String: NWConnection] = [:]
    
    let audioPort: UInt16 = 47830
    let discoveryPort: UInt16 = 47821
    
    var onPacketReceived: ((Data, String) -> Void)?
    
    func startDiscovery(instanceId: String, deviceName: String) {
        stop()
        discoveryTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            self?.sendDiscoveryBroadcast(instanceId: instanceId, deviceName: deviceName)
        }
        startListening()
    }
    
    func stop() {
        discoveryTimer?.invalidate()
        discoveryTimer = nil
        heartbeatTimer?.invalidate()
        heartbeatTimer = nil
        listener?.cancel()
        listener = nil
        for (_, conn) in activeConnections {
            conn.cancel()
        }
        activeConnections.removeAll()
        targetEndpoint = nil
    }
    
    private func getCachedConnection(to endpoint: NWEndpoint) -> NWConnection {
        let key = endpoint.debugDescription
        if let existing = activeConnections[key], existing.state == .ready {
            return existing
        }
        let conn = NWConnection(to: endpoint, using: .udp)
        conn.start(queue: .global())
        activeConnections[key] = conn
        return conn
    }
    
    private func sendDiscoveryBroadcast(instanceId: String, deviceName: String) {
        let port = NWEndpoint.Port(rawValue: discoveryPort) ?? NWEndpoint.Port(integerLiteral: 47821)
        let endpoint = NWEndpoint.hostPort(host: "255.255.255.255", port: port)
        let connection = NWConnection(to: endpoint, using: .udp)
        
        let json: [String: Any] = [
            "InstanceId": instanceId,
            "Name": deviceName,
            "AudioPort": audioPort,
            "CanSend": false,
            "CanReceive": true
        ]
        
        guard let data = try? JSONSerialization.data(withJSONObject: json) else { return }
        
        connection.start(queue: .main)
        connection.send(content: data, completion: .contentProcessed { _ in
            connection.cancel()
        })
    }
    
    private func startListening() {
        do {
            let parameters = NWParameters.udp
            parameters.allowLocalEndpointReuse = true
            let port = NWEndpoint.Port(rawValue: audioPort) ?? NWEndpoint.Port(integerLiteral: 47830)
            let listener = try NWListener(using: parameters, on: port)
            self.listener = listener
            
            listener.newConnectionHandler = { [weak self] (connection: NWConnection) in
                connection.start(queue: DispatchQueue.global())
                self?.receivePackets(on: connection)
            }
            listener.start(queue: DispatchQueue.global())
        } catch {
            print("Listener error: \(error)")
        }
    }
    
    private func receivePackets(on connection: NWConnection) {
        connection.receiveMessage { [weak self] data, context, isComplete, error in
            if let data = data, !data.isEmpty {
                var hostStr = ""
                if case let .hostPort(host, _) = connection.endpoint {
                    hostStr = host.debugDescription
                }
                self?.onPacketReceived?(data, hostStr)
            }
            if error == nil { self?.receivePackets(on: connection) }
        }
    }
    
    func startHeartbeat(to host: String, sequenceProvider: @escaping () -> UInt32) {
        heartbeatTimer?.invalidate()
        let port = NWEndpoint.Port(rawValue: audioPort) ?? NWEndpoint.Port(integerLiteral: 47830)
        let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(host), port: port)
        self.targetEndpoint = endpoint // FIXED: Assign to property
        
        heartbeatTimer = Timer.scheduledTimer(withTimeInterval: 10.0, repeats: true) { [weak self] _ in
            guard let ep = self?.targetEndpoint else { return }
            self?.sendPing(to: ep, sequence: sequenceProvider())
        }
    }
    
    private func sendPing(to endpoint: NWEndpoint, sequence: UInt32) {
        let connection = getCachedConnection(to: endpoint)
        var data = Data([0x52, 0x4D, 0x4E, 0x44, 1, 4, 1, 0])
        var seq = sequence.littleEndian
        withUnsafeBytes(of: seq) { data.append(contentsOf: $0) }
        data.append(0) // Kind 0 = Ping
        
        var timestamp = Int64(Date().timeIntervalSince1970 * 1000).littleEndian
        withUnsafeBytes(of: timestamp) { data.append(contentsOf: $0) }
        
        connection.send(content: data, completion: .contentProcessed { _ in })
    }
    
    func sendPong(to host: String, sequence: UInt32, originalTimestamp: Data) {
        let port = NWEndpoint.Port(rawValue: audioPort) ?? NWEndpoint.Port(integerLiteral: 47830)
        let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(host), port: port)
        let connection = getCachedConnection(to: endpoint)
        var data = Data([0x52, 0x4D, 0x4E, 0x44, 1, 4, 1, 0])
        var seq = sequence.littleEndian
        withUnsafeBytes(of: seq) { data.append(contentsOf: $0) }
        data.append(1) // Kind 1 = Pong
        data.append(originalTimestamp)
        
        connection.send(content: data, completion: .contentProcessed { _ in })
    }
    
    func sendControlPacket(to host: String, kind: UInt8, delta: UInt8 = 0, sequence: UInt32) {
        let port = NWEndpoint.Port(rawValue: audioPort) ?? NWEndpoint.Port(integerLiteral: 47830)
        let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(host), port: port)
        let connection = getCachedConnection(to: endpoint)
        var data = Data([0x52, 0x4D, 0x4E, 0x44, 1, 5, 1, 0])
        var seq = sequence.littleEndian
        withUnsafeBytes(of: seq) { data.append(contentsOf: $0) }
        data.append(kind)
        data.append(delta)
        
        connection.send(content: data, completion: .contentProcessed { _ in })
    }
}
