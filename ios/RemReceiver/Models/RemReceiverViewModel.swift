import Foundation
import Network
import AVFoundation

class RemReceiverViewModel: ObservableObject {
    @Published var isRunning = false
    @Published var targetSender: String = ""
    @Published var status: String = "Ready"
    @Published var volume: Float = 100.0
    @Published var bufferMs: Double = 50.0
    @Published var isMuted = false
    
    private let networkService = NetworkService()
    private let audioService = AudioService()
    private var sequence: UInt32 = 0
    private var encryptionKey: Data = Data(RemCrypto.emptyKey)
    private var currentFormat: AudioFormatInfo?
    
    init() {
        networkService.onPacketReceived = { [weak self] data, endpoint in
            self?.handlePacket(data: data, from: endpoint)
        }
    }
    
    func toggleReceiver() {
        if isRunning {
            stop()
        } else {
            start()
        }
    }
    
    func sendVolumeUp() {
        // Send control packet for volume up (RemoteControlKind.VolumeUp)
        networkService.sendControlPacket(kind: 0, sequence: sequence)
        sequence += 1
    }
    
    func sendVolumeDown() {
        // Send control packet for volume down (RemoteControlKind.VolumeDown)
        networkService.sendControlPacket(kind: 1, sequence: sequence)
        sequence += 1
    }
    
    func toggleMute() {
        isMuted.toggle()
        // Send control packet for mute toggle (RemoteControlKind.MuteToggle)
        networkService.sendControlPacket(kind: 2, sequence: sequence)
        sequence += 1
    }
    
    func updateBufferDuration() {
        audioService.setBufferDuration(bufferMs / 1000.0)
    }
    
    private func start() {
        isRunning = true
        status = "Connecting..."
        audioService.start()
        networkService.startDiscovery(instanceId: UUID().uuidString, deviceName: "iOS Receiver")
    }
    
    private func stop() {
        isRunning = false
        status = "Stopped"
        audioService.stop()
    }
    
    private func handlePacket(data: Data, from endpoint: NWEndpoint) {
        guard let header = RemHeader.decode(data: data) else { return }
        
        switch header.type {
        case .format:
            let payload = data.suffix(from: RemHeader.size)
            if let format = AudioFormatInfo.decode(data: payload) {
                let codecChanged = self.currentFormat?.codec != format.codec
                self.currentFormat = format
                
                if codecChanged {
                    // Reconfigure decoder or audio track if the codec changed (e.g., PCM <-> Opus)
                    self.audioService.reconfigure(for: format)
                }
                
                DispatchQueue.main.async {
                    let codecName = format.codec == 2 ? "Opus" : "PCM"
                    self.status = "Receiving \(codecName) (\(format.sampleRate)Hz)"
                }
            }
        case .audio:
            guard let format = currentFormat else { return }
            let payload = data.suffix(from: RemHeader.size)
            if let decrypted = RemCrypto.decrypt(key: encryptionKey, data: payload) {
                if format.codec == 2 {
                    processOpus(decrypted, format: format)
                } else {
                    processPcm(decrypted, format: format)
                }
            }
        default:
            break
        }
    }

    private func processOpus(_ data: Data, format: AudioFormatInfo) {
        // Implementation for Opus decoding using a library like SwiftOpus
    }

    private func processPcm(_ data: Data, format: AudioFormatInfo) {
        // Implementation for PCM reassembly and playout
    }
}
