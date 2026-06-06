import Foundation
import Network

class NetworkService: ObservableObject {
    private var connection: NWConnection?
    private var listener: NWListener?
    private var discoveryTimer: Timer?
    private var heartbeatTimer: Timer?
    private var targetEndpoint: NWEndpoint?
    
    let audioPort: UInt16 = 47830
    let discoveryPort: UInt16 = 47821
    
    @Published var discoveredSenders: [String: String] = [:] // InstanceId: Name
    
    var onPacketReceived: ((Data, NWEndpoint) -> Void)?
    
    func startDiscovery(instanceId: String, deviceName: String) {
        discoveryTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            self?.sendDiscoveryBroadcast(instanceId: instanceId, deviceName: deviceName)
        }
        
        startListening()
    }
    
    private func sendDiscoveryBroadcast(instanceId: String, deviceName: String) {
        let broadcastEndpoint = NWEndpoint.hostPort(host: "255.255.255.255", port: NWEndpoint.Port(rawValue: discoveryPort)!)
        let connection = NWConnection(to: broadcastEndpoint, using: .udp)
        
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
            
            let listener = try NWListener(using: parameters, on: NWEndpoint.Port(rawValue: audioPort)!)
            self.listener = listener
            
            listener.newConnectionHandler = { [weak self] (connection: NWConnection) in
                connection.start(queue: DispatchQueue.global())
                self?.receivePackets(on: connection)
            }
            
            listener.start(queue: DispatchQueue.global())
        } catch {
            print("Failed to start listener: \(error)")
        }
    }
    
    func connect(to host: String) {
        targetEndpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: audioPort)!)
        
        heartbeatTimer?.invalidate()
        heartbeatTimer = Timer.scheduledTimer(withTimeInterval: 10.0, repeats: true) { [weak self] _ in
            guard let endpoint = self?.targetEndpoint else { return }
            self?.sendHeartbeat(to: endpoint, sequence: 0)
        }
    }
    
    func sendControlPacket(kind: UInt8, sequence: UInt32) {
        // Implementation
    }
    
    private func receivePackets(on connection: NWConnection) {
        connection.receiveMessage { [weak self] data, context, isComplete, error in
            if let data = data, !data.isEmpty {
                self?.onPacketReceived?(data, connection.endpoint)
            }
            
            if error == nil {
                self?.receivePackets(on: connection)
            }
        }
    }
    
    func sendHeartbeat(to endpoint: NWEndpoint, sequence: UInt32) {
        // Implementation
    }
}
