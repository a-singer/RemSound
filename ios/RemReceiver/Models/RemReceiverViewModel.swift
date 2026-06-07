import Foundation
import Network
import AVFoundation
import UIKit
import Opus
import Combine

class RemReceiverViewModel: ObservableObject {
    @Published var isRunning = false
    @Published var targetSender: String = UserDefaults.standard.string(forKey: "targetSender") ?? "" {
        didSet { UserDefaults.standard.set(targetSender, forKey: "targetSender") }
    }
    @Published var password: String = UserDefaults.standard.string(forKey: "password") ?? "" {
        didSet {
            UserDefaults.standard.set(password, forKey: "password")
            scheduleKeyDerivation()
        }
    }
    @Published var status: String = "Ready"
    @Published var volume: Float = UserDefaults.standard.float(forKey: "volume") == 0
        ? 100.0
        : UserDefaults.standard.float(forKey: "volume") {
        didSet { UserDefaults.standard.set(volume, forKey: "volume") }
    }
    @Published var bufferMs: Double = UserDefaults.standard.double(forKey: "bufferMs") == 0
        ? 50.0
        : UserDefaults.standard.double(forKey: "bufferMs") {
        didSet {
            UserDefaults.standard.set(bufferMs, forKey: "bufferMs")
            updateBufferDuration()
        }
    }
    @Published var isLocalMuted = false
    @Published var logs: [String] = []

    private let networkService = NetworkService()
    private let audioService = AudioService()
    private let frameAssembler = PcmFrameAssembler()
    private var opusDecoder: Opus.Decoder?
    private var opusPacketCount = 0
    private var pcmPacketCount = 0
    private var sequence: UInt32 = 0
    private var encryptionKey: Data = Data(RemCrypto.emptyKey)
    private var currentFormat: AudioFormatInfo?
    private var keyGeneration = 0

    private var instanceId: String {
        if let id = UserDefaults.standard.string(forKey: "instanceId") { return id }
        let newId = UUID().uuidString
        UserDefaults.standard.set(newId, forKey: "instanceId")
        return newId
    }

    init() {
        LogService.shared.log("RemSound iOS Initialized (Instance: \(instanceId.prefix(8))...)")

        // Dispatch packet handling to the main thread so all AVAudioEngine graph operations
        // (reconfigure, start, stop) and mutable state access remain single-threaded.
        networkService.onPacketReceived = { [weak self] data, host in
            DispatchQueue.main.async {
                self?.handlePacket(data: data, from: host)
            }
        }
        audioService.onInterruption = { [weak self] interrupted in
            DispatchQueue.main.async {
                if !interrupted && self?.isRunning == true {
                    self?.audioService.start()
                }
            }
        }
        // Remote command callbacks arrive on a background thread; dispatch to main before
        // touching UIKit or any published state.
        audioService.onRemoteCommand = { [weak self] cmd in
            DispatchQueue.main.async {
                switch cmd {
                case "play": self?.start()
                case "pause", "stop": self?.stop()
                default: break
                }
            }
        }

        // Pre-derive key on background if a password is already saved.
        scheduleKeyDerivation()

        // Mirror LogService's rolling log array into this view model's @Published property.
        LogService.shared.$logs.assign(to: &$logs)
    }

    // Runs PBKDF2 on a background thread. Uses a generation counter so only the result
    // of the most recently requested derivation is applied (prevents stale results from
    // rapid password changes overlapping).
    private func scheduleKeyDerivation() {
        keyGeneration += 1
        let gen = keyGeneration
        let pwd = password
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let key = RemCrypto.deriveKey(password: pwd) else { return }
            DispatchQueue.main.async {
                guard self?.keyGeneration == gen else { return }
                self?.encryptionKey = key
            }
        }
    }

    func toggleReceiver() {
        isRunning ? stop() : start()
    }

    // delta=5 sends a 5-unit step to the Windows app per tap.
    func sendVolumeUp(delta: UInt8 = 5) { networkService.sendControlPacket(to: targetSender, kind: 0, delta: delta, sequence: sequence); sequence += 1 }
    func sendVolumeDown(delta: UInt8 = 5) { networkService.sendControlPacket(to: targetSender, kind: 1, delta: delta, sequence: sequence); sequence += 1 }
    func sendMuteToggle() { networkService.sendControlPacket(to: targetSender, kind: 2, delta: 0, sequence: sequence); sequence += 1 }

    func sendSystemVolumeUp(delta: UInt8 = 5) { networkService.sendControlPacket(to: targetSender, kind: 3, delta: delta, sequence: sequence); sequence += 1 }
    func sendSystemVolumeDown(delta: UInt8 = 5) { networkService.sendControlPacket(to: targetSender, kind: 4, delta: delta, sequence: sequence); sequence += 1 }
    func sendSystemMuteToggle() { networkService.sendControlPacket(to: targetSender, kind: 5, delta: 0, sequence: sequence); sequence += 1 }

    func updateBufferDuration() {
        audioService.setBufferDuration(bufferMs / 1000.0)
    }

    private func start() {
        isRunning = true
        UIApplication.shared.isIdleTimerDisabled = true
        status = targetSender.isEmpty ? "Searching..." : "Connecting to \(targetSender)..."
        LogService.shared.log("Starting receiver, target: \(targetSender.isEmpty ? "(auto-discover)" : targetSender)")

        // Kick off key derivation in background. Packets arriving before it completes will
        // use the currently cached key (correct if already derived for the same password).
        scheduleKeyDerivation()

        // Start discovery first — it internally calls stop() which would kill the heartbeat
        // timer if startHeartbeat were called first. Pass targetSender so discovery is also
        // unicast to the known Windows PC IP, bypassing routers that filter broadcast packets.
        networkService.startDiscovery(instanceId: instanceId, deviceName: UIDevice.current.name, unicastTarget: targetSender)

        if !targetSender.isEmpty {
            networkService.startHeartbeat(to: targetSender) { [weak self] in
                let s = self?.sequence ?? 0
                self?.sequence += 1
                return s
            }
        }

        audioService.start()
    }

    private func stop() {
        isRunning = false
        UIApplication.shared.isIdleTimerDisabled = false
        status = "Stopped"
        LogService.shared.log("Receiver stopped")
        audioService.stop()
        networkService.stop()
    }

    private func handlePacket(data: Data, from host: String) {
        if targetSender.isEmpty && !host.isEmpty {
            LogService.shared.log("Auto-detected sender at \(host)")
            targetSender = host
        }

        guard let header = RemHeader.decode(data: data) else { return }

        switch header.type {
        case .format:
            let payload = data.suffix(from: RemHeader.size)
            if let format = AudioFormatInfo.decode(data: payload) {
                let codecChanged = self.currentFormat?.codec != format.codec
                let rateChanged = self.currentFormat?.sampleRate != format.sampleRate
                self.currentFormat = format
                if codecChanged || rateChanged {
                    opusPacketCount = 0
                    pcmPacketCount = 0
                    if format.codec == 2 {
                        LogService.shared.log("Initializing Opus Decoder")
                        // swift-opus 0.0.2 requires interleaved format for multi-channel:
                        // isValidOpusPCMFormat returns false for non-interleaved stereo, which
                        // makes Decoder.init throw, try? returns nil, and opusDecoder stays nil
                        // forever — causing every format packet to re-trigger reconfigure().
                        guard let audioFormat = AVAudioFormat(
                            commonFormat: .pcmFormatFloat32,
                            sampleRate: Double(format.sampleRate),
                            channels: AVAudioChannelCount(format.channels),
                            interleaved: true
                        ) else {
                            LogService.shared.log("Invalid Opus format: \(format.sampleRate)Hz \(format.channels)ch")
                            return
                        }
                        opusDecoder = try? Opus.Decoder(format: audioFormat)
                        if opusDecoder == nil {
                            LogService.shared.log("Opus Decoder init failed — audio will be silent")
                            return
                        }
                    }
                    // reconfigure() is safe to call here because onPacketReceived is dispatched to main.
                    self.audioService.reconfigure(for: format)
                }
                self.status = "Receiving \(format.codec == 2 ? "Opus" : "PCM") (\(format.sampleRate)Hz)"
                LogService.shared.log("Format: \(format.codec == 2 ? "Opus" : "PCM") \(format.sampleRate)Hz \(format.channels)ch")
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
            if payload.count >= 9 && payload[0] == 0 {
                let timestamp = payload.suffix(from: 1).prefix(8)
                networkService.sendPong(to: host, sequence: sequence, originalTimestamp: timestamp)
                sequence += 1
            }
        default: break
        }
    }

    private func processOpus(_ data: Data, format: AudioFormatInfo) {
        guard !isLocalMuted, let decoder = opusDecoder else { return }
        opusPacketCount += 1
        let logThis = opusPacketCount == 1
        if logThis { LogService.shared.log("First Opus packet: \(data.count) bytes encrypted") }
        do {
            let decodedBuffer = try decoder.decode(data)
            let channelCount = max(1, Int(format.channels))
            let frameCount = Int(decodedBuffer.frameLength)
            if logThis { LogService.shared.log("Decoded: \(frameCount) frames \(channelCount)ch; decoded format: \(decodedBuffer.format.sampleRate)Hz interleaved=\(decodedBuffer.format.isInterleaved)") }
            guard frameCount > 0 else { return }

            // Output buffer uses standardFormatWithSampleRate (non-interleaved) as required by
            // AVAudioEngine's scheduleBuffer. The decoded buffer is interleaved because the
            // decoder was initialized with interleaved: true (swift-opus 0.0.2 requirement).
            guard let audioFormat = AVAudioFormat(
                standardFormatWithSampleRate: Double(format.sampleRate),
                channels: AVAudioChannelCount(channelCount)
            ), let pcmBuffer = AVAudioPCMBuffer(
                pcmFormat: audioFormat,
                frameCapacity: AVAudioFrameCount(frameCount)
            ) else { return }

            pcmBuffer.frameLength = AVAudioFrameCount(frameCount)
            if logThis { LogService.shared.log("Output buffer: \(pcmBuffer.format.sampleRate)Hz \(pcmBuffer.format.channelCount)ch interleaved=\(pcmBuffer.format.isInterleaved)") }
            let multiplier = self.volume / 100.0

            // floatChannelData?[0] of an interleaved buffer contains all channels inline:
            // [L0, R0, L1, R1, ...] for stereo. Deinterleave into separate channel arrays.
            guard let src = decodedBuffer.floatChannelData?[0] else { return }
            if channelCount == 1 {
                guard let dst = pcmBuffer.floatChannelData?[0] else { return }
                for i in 0..<frameCount { dst[i] = src[i] * multiplier }
            } else {
                for frame in 0..<frameCount {
                    for ch in 0..<channelCount {
                        pcmBuffer.floatChannelData?[ch][frame] = src[frame * channelCount + ch] * multiplier
                    }
                }
            }
            audioService.scheduleBuffer(pcmBuffer)
        } catch {
            LogService.shared.log("Opus decode error: \(error)")
        }
    }

    private func processPcm(_ data: Data, format: AudioFormatInfo) {
        guard !isLocalMuted else { return }
        pcmPacketCount += 1
        let logThis = pcmPacketCount == 1
        if logThis { LogService.shared.log("First PCM frame: \(data.count) bytes plaintext") }
        let channelCount = max(1, Int(format.channels))
        let totalSamples = data.count / 3
        // Wire format is interleaved: L0, R0, L1, R1, ... for stereo.
        let frameCount = totalSamples / channelCount
        guard frameCount > 0 else { return }
        let multiplier = self.volume / 100.0

        guard let audioFormat = AVAudioFormat(
            standardFormatWithSampleRate: Double(format.sampleRate),
            channels: AVAudioChannelCount(channelCount)
        ), let pcmBuffer = AVAudioPCMBuffer(pcmFormat: audioFormat, frameCapacity: AVAudioFrameCount(frameCount)) else { return }
        pcmBuffer.frameLength = pcmBuffer.frameCapacity

        for i in 0..<totalSamples {
            let offset = i * 3
            guard offset + 2 < data.count else { break }
            let b0 = Int32(data[offset])
            let b1 = Int32(data[offset + 1])
            let b2 = Int32(Int8(bitPattern: data[offset + 2]))
            let sample32 = (b2 << 16) | (b1 << 8) | b0
            // 8388607.0 = 2^23 - 1, matching the sender's scale factor.
            let floatSample = (Float(sample32) / 8388607.0) * multiplier

            // Deinterleave: sample i belongs to channel (i % channels) at frame (i / channels).
            let ch = channelCount > 1 ? i % channelCount : 0
            let frame = channelCount > 1 ? i / channelCount : i
            if frame < frameCount {
                pcmBuffer.floatChannelData?[ch][frame] = floatSample
            }
        }
        audioService.scheduleBuffer(pcmBuffer)
    }
}
