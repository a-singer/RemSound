import Foundation
import AVFoundation
import MediaPlayer

class AudioService {
    private var audioEngine: AVAudioEngine
    private var playerNode: AVAudioPlayerNode
    private var currentFormat: AudioFormatInfo?
    
    var onInterruption: ((Bool) -> Void)?
    
    init() {
        self.audioEngine = AVAudioEngine()
        self.playerNode = AVAudioPlayerNode()
        audioEngine.attach(playerNode)
        setupAudioSession()
        setupNotifications()
    }
    
    private func setupAudioSession() {
        let session = AVAudioSession.sharedInstance()
        do {
            try session.setCategory(.playback, mode: .measurement, options: [.mixWithOthers, .duckOthers])
            try session.setActive(true)
        } catch {
            print("Session error: \(error)")
        }
    }
    
    private func setupNotifications() {
        NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            guard let typeValue = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
                  let type = AVAudioSession.InterruptionType(rawValue: typeValue) else { return }
            
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
    
    func reconfigure(for format: AudioFormatInfo) {
        stop()
        let hwFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                    sampleRate: Double(format.sampleRate),
                                    channels: AVAudioChannelCount(format.channels),
                                    interleaved: false)!
        
        audioEngine.connect(playerNode, to: audioEngine.mainMixerNode, format: hwFormat)
        currentFormat = format
        start()
    }
    
    func start() {
        do {
            if !audioEngine.isRunning {
                try audioEngine.start()
            }
            playerNode.play()
            updateNowPlaying()
        } catch {
            print("Engine start error: \(error)")
        }
    }
    
    func stop() {
        playerNode.stop()
        audioEngine.stop()
    }
    
    func setBufferDuration(_ duration: Double) {
        // Preferred buffer is just a hint. 
        // Real jitter buffer should be in ViewModel/Service logic if needed.
        try? AVAudioSession.sharedInstance().setPreferredIOBufferDuration(duration)
    }
    
    func scheduleBuffer(_ buffer: AVAudioPCMBuffer) {
        playerNode.scheduleBuffer(buffer)
    }
    
    private func updateNowPlaying() {
        let center = MPNowPlayingInfoCenter.default()
        var info = [String: Any]()
        info[MPMediaItemPropertyTitle] = "RemSound Receiver"
        info[MPMediaItemPropertyArtist] = "Windows PC"
        center.nowPlayingInfo = info
    }
}
