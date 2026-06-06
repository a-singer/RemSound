import Foundation
import Network
import AVFoundation
import UIKit
import Opus

class RemReceiverViewModel: ObservableObject {
    @Published var isRunning = false
    @Published var targetSender: String = UserDefaults.standard.string(forKey: "targetSender") ?? "" {
        didSet { UserDefaults.standard.set(targetSender, forKey: "targetSender") }
    }
    @Published var password: String = UserDefaults.standard.string(forKey: "password") ?? "" {
        didSet { 
            UserDefaults.standard.set(password, forKey: "password")
            updateEncryptionKey() 
        }
    }
    @Published var status: String = "Ready"
    @Published var volume: Float = UserDefaults.standard.float(forKey: "volume") == 0 ? 100.0 : UserDefaults.standard.float(forKey: "volume") {
        didSet { UserDefaults.standard.set(volume, forKey: "volume") }
    }
    @Published var bufferMs: Double = UserDefaults.standard.double(forKey: "bufferMs") == 0 ? 50.0 : UserDefaults.standard.double(forKey: "bufferMs") {
        didSet { 
            UserDefaults.standard.set(bufferMs, forKey: "bufferMs")
            updateBufferDuration()
        }
    }
    @Published var isLocalMuted = false
    
    private let networkService = NetworkService()
    private let audioService = AudioService()
    private let frameAssembler = PcmFrameAssembler()
    private var opusDecoder: Opus.Decoder?
    private var sequence: UInt32 = 0
    private var encryptionKey: Data = Data(RemCrypto.emptyKey)
    private var currentFormat: AudioFormatInfo?
    
    private var instanceId: String {
        if let id = UserDefaults.standard.string(forKey: "instanceId") { return id }
        let newId = UUID().uuidString
        UserDefaults.standard.set(newId, forKey: "instanceId")
        return newId
    }
    
    init() {
        networkService.onPacketReceived = { [weak self] data, host in
            self?.handlePacket(data: data, from: host)
        }
        audioService.onInterruption = { [weak self] interrupted in
            if !interrupted && self?.isRunning == true {
                self?.audioService.start()
            }
        }
    }
    
    private func updateEncryptionKey() {
        if let key = RemCrypto.deriveKey(password: password) {
            encryptionKey = key
        }
    }
    
    func toggleReceiver() {
        isRunning ? stop() : start()
    }
    
    func sendVolumeUp() { networkService.sendControlPacket(to: targetSender, kind: 0, sequence: sequence); sequence += 1 }
    func sendVolumeDown() { networkService.sendControlPacket(to: targetSender, kind: 1, sequence: sequence); sequence += 1 }
    func sendMuteToggle() { networkService.sendControlPacket(to: targetSender, kind: 2, sequence: sequence); sequence += 1 }
    
    func sendSystemVolumeUp() { networkService.sendControlPacket(to: targetSender, kind: 3, sequence: sequence); sequence += 1 }
    func sendSystemVolumeDown() { networkService.sendControlPacket(to: targetSender, kind: 4, sequence: sequence); sequence += 1 }
    func sendSystemMuteToggle() { networkService.sendControlPacket(to: targetSender, kind: 5, sequence: sequence); sequence += 1 }
    
    func updateBufferDuration() {
        audioService.setBufferDuration(bufferMs / 1000.0)
    }
    
    private func start() {
        isRunning = true
        updateEncryptionKey()
        UIApplication.shared.isIdleTimerDisabled = true
        status = targetSender.isEmpty ? "Searching..." : "Connecting to \(targetSender)..."
        if !targetSender.isEmpty {
            networkService.startHeartbeat(to: targetSender) { [weak self] in 
                let s = self?.sequence ?? 0
                self?.sequence += 1
                return s
            }
        }
        audioService.start()
        networkService.startDiscovery(instanceId: instanceId, deviceName: UIDevice.current.name)
    }
    
    private func stop() {
        isRunning = false
        UIApplication.shared.isIdleTimerDisabled = false
        status = "Stopped"
        audioService.stop()
        networkService.stop()
    }
    
    private func handlePacket(data: Data, from host: String) {
        if targetSender.isEmpty && !host.isEmpty {
            DispatchQueue.main.async { self.targetSender = host }
        }
        
        guard let header = RemHeader.decode(data: data) else { return }
        
        switch header.type {
        case .format:
            let payload = data.suffix(from: RemHeader.size)
            if let format = AudioFormatInfo.decode(data: payload) {
                let codecChanged = self.currentFormat?.codec != format.codec
                let rateChanged = self.currentFormat?.sampleRate != format.sampleRate
                self.currentFormat = format
                if codecChanged || rateChanged || opusDecoder == nil {
                    if format.codec == 2 {
                        let audioFormat = AVAudioFormat(standardFormatWithSampleRate: Double(format.sampleRate), channels: AVAudioChannelCount(format.channels))!
                        opusDecoder = try? Opus.Decoder(format: audioFormat)
                    }
                    self.audioService.reconfigure(for: format)
                }
                DispatchQueue.main.async {
                    self.status = "Receiving \(format.codec == 2 ? "Opus" : "PCM") (\(format.sampleRate)Hz)"
                }
            }
        case .audio:
            guard let format = currentFormat else { return }
            let payload = data.suffix(from: RemHeader.size)
            if format.codec == 2 {
                if let decrypted = RemCrypto.decrypt(key: encryptionKey, data: payload) {
                    processOpus(decrypted, format: format)
                }
            } else {
                guard payload.count > 6 else { return }
                let frameId = payload.withUnsafeBytes { $0.load(as: UInt32.self).littleEndian }
                let partIndex = Int(payload[4])
                let totalParts = Int(payload[5])
                let partData = payload.suffix(from: 6)
                if let fullFrame = frameAssembler.addPart(frameId: frameId, partIndex: partIndex, totalParts: totalParts, data: partData) {
                    if let decrypted = RemCrypto.decrypt(key: encryptionKey, data: fullFrame) {
                        processPcm(decrypted, format: format)
                    }
                }
            }
        case .heartbeat:
            let payload = data.suffix(from: RemHeader.size)
            if payload.count >= 9 && payload[0] == 0 { // Ping = 0
                let timestamp = payload.suffix(from: 1).prefix(8)
                networkService.sendPong(to: host, sequence: sequence, originalTimestamp: timestamp)
                sequence += 1
            }
        default: break
        }
    }

    private func processOpus(_ data: Data, format: AudioFormatInfo) {
        guard !isLocalMuted, let decoder = opusDecoder else { return }
        do {
            let pcmBuffer = try decoder.decode(data)
            let multiplier = self.volume / 100.0
            if multiplier != 1.0 {
                for ch in 0..<Int(pcmBuffer.format.channelCount) {
                    let ptr = pcmBuffer.floatChannelData?[ch]
                    for frame in 0..<Int(pcmBuffer.frameLength) {
                        ptr?[frame] *= multiplier
                    }
                }
            }
            audioService.scheduleBuffer(pcmBuffer) // DIRECT FLOAT PASS (FIXED)
        } catch { print("Opus error: \(error)") }
    }

    private func processPcm(_ data: Data, format: AudioFormatInfo) {
        guard !isLocalMuted else { return }
        let sampleCount = data.count / 3
        let multiplier = self.volume / 100.0
        
        let audioFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Double(format.sampleRate), channels: AVAudioChannelCount(format.channels), interleaved: false)!
        let pcmBuffer = AVAudioPCMBuffer(pcmFormat: audioFormat, frameCapacity: AVAudioFrameCount(sampleCount))!
        pcmBuffer.frameLength = pcmBuffer.frameCapacity
        
        for i in 0..<sampleCount {
            let offset = i * 3
            let b0 = Int32(data[offset])
            let b1 = Int32(data[offset + 1])
            let b2 = Int32(Int8(bitPattern: data[offset + 2]))
            let sample32 = (b2 << 16) | (b1 << 8) | b0
            let floatSample = (Float(sample32) / 8388608.0) * multiplier
            
            for ch in 0..<Int(format.channels) {
                pcmBuffer.floatChannelData?[ch][i] = floatSample
            }
        }
        audioService.scheduleBuffer(pcmBuffer)
    }
}
