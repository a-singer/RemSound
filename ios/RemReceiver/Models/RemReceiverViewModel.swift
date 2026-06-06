import Foundation
import Network
import AVFoundation
import UIKit
import Opus

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
    private var opusDecoder: Opus.Decoder?
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
                        let audioFormat = AVAudioFormat(standardFormatWithSampleRate: Double(format.sampleRate), channels: AVAudioChannelCount(format.channels))!
                        opusDecoder = try? Opus.Decoder(format: audioFormat)
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
        do {
            let pcmBuffer = try decoder.decode(data)
            
            // Apply volume if needed
            let multiplier = self.volume / 100.0
            if multiplier != 1.0 {
                let channelCount = Int(pcmBuffer.format.channelCount)
                let frameLength = Int(pcmBuffer.frameLength)
                for ch in 0..<channelCount {
                    let ptr = pcmBuffer.floatChannelData?[ch]
                    for frame in 0..<frameLength {
                        ptr?[frame] *= multiplier
                    }
                }
            }
            
            // Convert to Int16 samples for AudioService if it expects Int16
            // Or update AudioService to handle Float/PCMBuffer directly.
            // My current AudioService.scheduleBuffer takes [Int16].
            
            var int16Samples = [Int16]()
            let channels = Int(pcmBuffer.format.channelCount)
            let frames = Int(pcmBuffer.frameLength)
            int16Samples.reserveCapacity(frames * channels)
            
            for frame in 0..<frames {
                for ch in 0..<channels {
                    let floatSample = pcmBuffer.floatChannelData?[ch][frame] ?? 0
                    let int16Sample = Int16(max(-32768, min(32767, floatSample * 32767)))
                    int16Samples.append(int16Sample)
                }
            }
            
            audioService.scheduleBuffer(pcmData: int16Samples, format: pcmBuffer.format)
            
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
