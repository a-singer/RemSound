import Foundation
import Network
import AVFoundation
import UIKit
import SwiftOpus

class RemReceiverViewModel: ObservableObject {
    @Published var isRunning = false
    @Published var targetSender: String = ""
    @Published var password: String = "" {
        didSet {
            updateEncryptionKey()
        }
    }
    @Published var status: String = "Ready"
    @Published var volume: Float = 100.0
    @Published var bufferMs: Double = 50.0
    @Published var isMuted = false
    
    private let networkService = NetworkService()
    private let audioService = AudioService()
    private var opusDecoder: OpusDecoder?
    private var sequence: UInt32 = 0
    private var encryptionKey: Data = Data(RemCrypto.emptyKey)
    private var currentFormat: AudioFormatInfo?
    
    init() {
        networkService.onPacketReceived = { [weak self] data, endpoint in
            self?.handlePacket(data: data, from: endpoint)
        }
    }
    
    private func updateEncryptionKey() {
        if password.isEmpty {
            encryptionKey = Data(RemCrypto.emptyKey)
        } else {
            // Key derivation logic placeholder
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
        networkService.sendControlPacket(kind: 0, sequence: sequence)
        sequence += 1
    }
    
    func sendVolumeDown() {
        networkService.sendControlPacket(kind: 1, sequence: sequence)
        sequence += 1
    }
    
    func toggleMute() {
        isMuted.toggle()
        networkService.sendControlPacket(kind: 2, sequence: sequence)
        sequence += 1
    }
    
    func updateBufferDuration() {
        audioService.setBufferDuration(bufferMs / 1000.0)
    }
    
    private func start() {
        isRunning = true
        UIApplication.shared.isIdleTimerDisabled = true
        if targetSender.isEmpty {
            status = "Searching for Senders..."
        } else {
            status = "Connecting to \(targetSender)..."
            networkService.connect(to: targetSender)
        }
        audioService.start()
        networkService.startDiscovery(instanceId: UUID().uuidString, deviceName: "iOS Receiver")
    }
    
    private func stop() {
        isRunning = false
        UIApplication.shared.isIdleTimerDisabled = false
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
                
                if codecChanged || opusDecoder == nil {
                    if format.codec == 2 {
                        opusDecoder = try? OpusDecoder(sampleRate: format.sampleRate, channels: format.channels)
                    }
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
        guard let decoder = opusDecoder else { return }
        let frameSize = Int(format.frameSamplesPerChannel)
        let channels = Int(format.channels)
        
        var output = [Int16](repeating: 0, count: frameSize * channels)
        do {
            let decodedCount = try decoder.decode(data, outPcm: &output)
            if decodedCount > 0 {
                let multiplier = self.volume / 100.0
                if multiplier != 1.0 {
                    for i in 0..<output.count {
                        var sample = Float(output[i]) * multiplier
                        sample = max(-32768, min(32767, sample))
                        output[i] = Int16(sample)
                    }
                }
                
                if let audioFormat = AVAudioFormat(commonFormat: .pcmFormatInt16,
                                                  sampleRate: Double(format.sampleRate),
                                                  channels: AVAudioChannelCount(format.channels),
                                                  interleaved: true) {
                    audioService.scheduleBuffer(pcmData: output, format: audioFormat)
                }
            }
        } catch {
            print("Opus decode error: \(error)")
        }
    }

    private func processPcm(_ data: Data, format: AudioFormatInfo) {
        let bytesPerSample = 3
        let sampleCount = data.count / bytesPerSample
        var samples = [Int16]()
        samples.reserveCapacity(sampleCount)
        
        let multiplier = self.volume / 100.0
        
        for i in 0..<sampleCount {
            let offset = i * bytesPerSample
            let b0 = Int32(data[offset])
            let b1 = Int32(data[offset + 1])
            let b2 = Int32(Int8(bitPattern: data[offset + 2])) 
            
            let sample32 = (b2 << 16) | (b1 << 8) | b0
            var sampleFloat = Float(sample32) * multiplier / 256.0
            sampleFloat = max(-32768, min(32767, sampleFloat))
            samples.append(Int16(sampleFloat))
        }
        
        if let audioFormat = AVAudioFormat(commonFormat: .pcmFormatInt16,
                                          sampleRate: Double(format.sampleRate),
                                          channels: AVAudioChannelCount(format.channels),
                                          interleaved: true) {
            audioService.scheduleBuffer(pcmData: samples, format: audioFormat)
        }
    }
}
