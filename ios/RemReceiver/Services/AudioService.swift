import Foundation
import AVFoundation
import MediaPlayer

class AudioService {
    private var audioEngine: AVAudioEngine
    private var playerNode: AVAudioPlayerNode
    private var currentFormat: AudioFormatInfo?

    private var playTarget: Any?
    private var pauseTarget: Any?
    private var stopTarget: Any?

    var onInterruption: ((Bool) -> Void)?
    var onRemoteCommand: ((String) -> Void)?

    init() {
        LogService.shared.log("Initializing Audio Engine...")
        self.audioEngine = AVAudioEngine()
        self.playerNode = AVAudioPlayerNode()
        audioEngine.attach(playerNode)
        // Connect with a default format so the engine can start immediately before the first
        // format packet arrives. reconfigure() will re-connect with the real format later.
        let defaultFormat = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
        audioEngine.connect(playerNode, to: audioEngine.mainMixerNode, format: defaultFormat)
        setupAudioSession()
        setupNotifications()
        setupRemoteCommands()
    }

    private func setupAudioSession() {
        let session = AVAudioSession.sharedInstance()
        do {
            try session.setCategory(.playback, mode: .default, options: [.mixWithOthers])
            try session.setActive(true)
            LogService.shared.log("Audio Session active (.playback)")
        } catch {
            LogService.shared.log("Audio session error: \(error)")
        }
    }

    private func setupNotifications() {
        NotificationCenter.default.addObserver(
            forName: AVAudioSession.interruptionNotification, object: nil, queue: .main
        ) { [weak self] note in
            guard let typeValue = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
                  let type = AVAudioSession.InterruptionType(rawValue: typeValue) else { return }
            LogService.shared.log("Audio Interruption: \(type == .began ? "Began" : "Ended")")
            if type == .began {
                self?.onInterruption?(true)
            } else if type == .ended {
                if let optionsValue = note.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt,
                   AVAudioSession.InterruptionOptions(rawValue: optionsValue).contains(.shouldResume) {
                    self?.onInterruption?(false)
                }
            }
        }
    }

    private func setupRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        playTarget = center.playCommand.addTarget { [weak self] _ in
            self?.onRemoteCommand?("play")
            return .success
        }
        pauseTarget = center.pauseCommand.addTarget { [weak self] _ in
            self?.onRemoteCommand?("pause")
            return .success
        }
        stopTarget = center.stopCommand.addTarget { [weak self] _ in
            self?.onRemoteCommand?("stop")
            return .success
        }
    }

    // Must be called on the main thread. All AVAudioEngine graph operations (connect, start, stop)
    // are not thread-safe; the caller (handlePacket via onPacketReceived) is dispatched to main.
    func reconfigure(for format: AudioFormatInfo) {
        LogService.shared.log("Reconfiguring audio for \(format.sampleRate)Hz, \(format.channels)ch, codec \(format.codec)")
        stop()
        guard let hwFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: Double(format.sampleRate),
            channels: AVAudioChannelCount(format.channels),
            interleaved: false
        ) else {
            LogService.shared.log("Ignoring invalid format: \(format.sampleRate)Hz \(format.channels)ch")
            return
        }
        audioEngine.connect(playerNode, to: audioEngine.mainMixerNode, format: hwFormat)
        currentFormat = format
        start()
    }

    func start() {
        do {
            if !audioEngine.isRunning {
                try audioEngine.start()
                LogService.shared.log("Audio Engine started")
            }
            playerNode.play()
            updateNowPlaying(active: true)
        } catch {
            LogService.shared.log("Engine start error: \(error)")
        }
    }

    func stop() {
        playerNode.stop()
        audioEngine.stop()
        updateNowPlaying(active: false)
        LogService.shared.log("Audio Engine stopped")
    }

    func setBufferDuration(_ duration: Double) {
        try? AVAudioSession.sharedInstance().setPreferredIOBufferDuration(duration)
    }

    func scheduleBuffer(_ buffer: AVAudioPCMBuffer) {
        playerNode.scheduleBuffer(buffer)
    }

    private func updateNowPlaying(active: Bool) {
        var info = [String: Any]()
        info[MPMediaItemPropertyTitle] = "RemSound Receiver"
        info[MPMediaItemPropertyArtist] = "Windows PC"
        info[MPNowPlayingInfoPropertyPlaybackRate] = active ? 1.0 : 0.0
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
    }

    deinit {
        let center = MPRemoteCommandCenter.shared()
        center.playCommand.removeTarget(playTarget)
        center.pauseCommand.removeTarget(pauseTarget)
        center.stopCommand.removeTarget(stopTarget)
    }
}
