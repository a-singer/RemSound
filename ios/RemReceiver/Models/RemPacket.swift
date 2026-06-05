import Foundation

enum RemPacketType: UInt8 {
    case format = 1
    case audio = 2
    case keepAlive = 3
    case heartbeat = 4
    case control = 5
}

struct RemHeader {
    let type: RemPacketType
    let streamId: UInt16
    let sequence: UInt32
    
    static let magic: UInt32 = 0x444E4D52
    static let version: UInt8 = 1
    static let size = 12
    
    static func decode(data: Data) -> RemHeader? {
        guard data.count >= size else { return nil }
        
        let magic = data.withUnsafeBytes { $0.load(as: UInt32.self).littleEndian }
        guard magic == self.magic else { return nil }
        
        let version = data[4]
        guard version == self.version else { return nil }
        
        guard let type = RemPacketType(rawValue: data[5]) else { return nil }
        
        let streamId = data.withUnsafeBytes { $0.load(fromByteOffset: 6, as: UInt16.self).littleEndian }
        let sequence = data.withUnsafeBytes { $0.load(fromByteOffset: 8, as: UInt32.self).littleEndian }
        
        return RemHeader(type: type, streamId: streamId, sequence: sequence)
    }
}

struct AudioFormatInfo {
    let sampleRate: Int32
    let channels: Int32
    let bitsPerSample: Int32
    let encoding: Int32
    let blockAlign: Int32
    let averageBytesPerSecond: Int32
    let codec: Int32
    let frameSamplesPerChannel: Int32
    let lane: UInt8
    
    static func decode(data: Data) -> AudioFormatInfo? {
        guard data.count >= 32 else { return nil }
        
        let sampleRate = data.withUnsafeBytes { $0.load(as: Int32.self).littleEndian }
        let channels = data.withUnsafeBytes { $0.load(fromByteOffset: 4, as: Int32.self).littleEndian }
        let bitsPerSample = data.withUnsafeBytes { $0.load(fromByteOffset: 8, as: Int32.self).littleEndian }
        let encoding = data.withUnsafeBytes { $0.load(fromByteOffset: 12, as: Int32.self).littleEndian }
        let blockAlign = data.withUnsafeBytes { $0.load(fromByteOffset: 16, as: Int32.self).littleEndian }
        let averageBytesPerSecond = data.withUnsafeBytes { $0.load(fromByteOffset: 20, as: Int32.self).littleEndian }
        let codec = data.withUnsafeBytes { $0.load(fromByteOffset: 24, as: Int32.self).littleEndian }
        let frameSamplesPerChannel = data.withUnsafeBytes { $0.load(fromByteOffset: 28, as: Int32.self).littleEndian }
        
        var lane: UInt8 = 0
        if data.count >= 36 {
            lane = data[32]
        }
        
        return AudioFormatInfo(
            sampleRate: sampleRate,
            channels: channels,
            bitsPerSample: bitsPerSample,
            encoding: encoding,
            blockAlign: blockAlign,
            averageBytesPerSecond: averageBytesPerSecond,
            codec: codec,
            frameSamplesPerChannel: frameSamplesPerChannel,
            lane: lane
        )
    }
}
