import Foundation
import AVFoundation

class AudioService {
    private var audioEngine: AVAudioEngine
    private var playerNode: AVAudioPlayerNode
    
    init() {
        self.audioEngine = AVAudioEngine()
        self.playerNode = AVAudioPlayerNode()
        audioEngine.attach(playerNode)
        
        setupAudioSession()
    }
    
    private func setupAudioSession() {
        let session = AVAudioSession.sharedInstance()
        do {
            // Background audio support + Low Latency category
            try session.setCategory(.playback, mode: .measurement, options: [.mixWithOthers])
            try session.setPreferredIOBufferDuration(0.005) // 5ms buffer for low latency
            try session.setActive(true)
        } catch {
            print("Audio session setup failed: \(error)")
        }
    }
    
    func start() {
        do {
            try audioEngine.start()
            playerNode.play()
        } catch {
            print("Audio engine start failed: \(error)")
        }
    }
    
    func stop() {
        playerNode.stop()
        audioEngine.stop()
    }
    
    func scheduleBuffer(pcmData: [Int16], format: AVAudioFormat) {
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(pcmData.count / Int(format.channelCount))) else { return }
        
        buffer.frameLength = buffer.frameCapacity
        let channels = Int(format.channelCount)
        
        for ch in 0..<channels {
            let channelData = buffer.int16ChannelData?[ch]
            for frame in 0..<Int(buffer.frameLength) {
                channelData?[frame] = pcmData[frame * channels + ch]
            }
        }
        
        playerNode.scheduleBuffer(buffer)
    }
}
